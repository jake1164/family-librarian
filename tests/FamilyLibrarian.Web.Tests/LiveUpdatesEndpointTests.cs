using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Realtime;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Realtime;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class LiveUpdatesEndpointTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task RequestScanAndPublishingChangesReachOnlyTheOwnerAndAdmins()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        await using var admin = await Viewer.ConnectAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await using var owner = await Viewer.ConnectAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        var unrelatedEmail = await CreateUserAsync(factory);
        await using var unrelated = await Viewer.ConnectAsync(factory, unrelatedEmail, WebTestFixture.UserPassword);
        var resolved = await owner.Http.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", null);
        resolved.EnsureSuccessStatusCode();
        var work = await resolved.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new BookRequest(owner.UserId, work.Id, [RequestMediaType.Ebook], null, DateTimeOffset.UtcNow);
        database.BookRequests.Add(request);
        await database.SaveChangesAsync();
        var received = await BarrierAsync(factory, admin, owner, unrelated);
        Assert.AreEqual(LiveUpdateTopics.Requests, received[0]);
        Assert.AreEqual(LiveUpdateTopics.Requests, received[1]);
        Assert.AreEqual(LiveUpdateTopics.None, received[2]);

        var asset = new MediaAsset(work.Id, null, RequestMediaType.Ebook, ".epub", "live.epub", "live.epub", 1,
            new string('a', 64), "application/epub+zip", request.Formats.Single().Id, null, DateTimeOffset.UtcNow);
        database.MediaAssets.Add(asset);
        database.SecurityEvaluations.Add(new SecurityEvaluation(asset.Id, "test", DateTimeOffset.UtcNow));
        await database.SaveChangesAsync();
        received = await BarrierAsync(factory, admin, owner, unrelated);
        Assert.AreEqual(LiveUpdateTopics.Security | LiveUpdateTopics.Requests, received[0]);
        Assert.AreEqual(LiveUpdateTopics.Requests, received[1]);
        Assert.AreEqual(LiveUpdateTopics.None, received[2]);

        database.LibraryImports.Add(new LibraryImport(asset.Id, DateTimeOffset.UtcNow));
        await database.SaveChangesAsync();
        received = await BarrierAsync(factory, admin, owner, unrelated);
        Assert.AreEqual(LiveUpdateTopics.Publishing | LiveUpdateTopics.Requests, received[0]);
        Assert.AreEqual(LiveUpdateTopics.Requests, received[1]);
        Assert.AreEqual(LiveUpdateTopics.None, received[2]);
    }

    [TestMethod]
    public async Task NotificationAudienceAndReceiptsRemainPrivateAcrossLiveConnections()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        await using var admin = await Viewer.ConnectAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await using var owner = await Viewer.ConnectAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = Notification(owner.UserId);
        database.NotificationEvents.Add(notification);
        await database.SaveChangesAsync();
        var received = await BarrierAsync(factory, admin, owner);
        Assert.AreEqual(LiveUpdateTopics.None, received[0]);
        Assert.AreEqual(LiveUpdateTopics.Notifications, received[1]);

        database.NotificationEvents.Add(Notification(null));
        await database.SaveChangesAsync();
        received = await BarrierAsync(factory, admin, owner);
        Assert.AreEqual(LiveUpdateTopics.Notifications, received[0]);
        Assert.AreEqual(LiveUpdateTopics.None, received[1]);

        var receipt = new NotificationReceipt(notification.Id, owner.UserId);
        receipt.MarkRead(DateTimeOffset.UtcNow);
        database.NotificationReceipts.Add(receipt);
        await database.SaveChangesAsync();
        received = await BarrierAsync(factory, admin, owner);
        Assert.AreEqual(LiveUpdateTopics.None, received[0]);
        Assert.AreEqual(LiveUpdateTopics.Notifications, received[1]);
    }

    [TestMethod]
    public async Task ExplicitTransactionsNotifyOnlyAfterCommitAndNeverAfterRollbackOrDisposal()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        await using var viewer = await Viewer.ConnectAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            database.NotificationEvents.Add(Notification(viewer.UserId));
            await database.SaveChangesAsync();
            Assert.AreEqual(LiveUpdateTopics.None, (await BarrierAsync(factory, viewer))[0]);
            await transaction.CommitAsync();
        }
        Assert.AreEqual(LiveUpdateTopics.Notifications, (await BarrierAsync(factory, viewer))[0]);

        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            database.NotificationEvents.Add(Notification(viewer.UserId));
            await database.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        Assert.AreEqual(LiveUpdateTopics.None, (await BarrierAsync(factory, viewer))[0]);

        await using (await database.Database.BeginTransactionAsync())
        {
            database.NotificationEvents.Add(Notification(viewer.UserId));
            await database.SaveChangesAsync();
            // Disposing an uncommitted transaction also must discard buffered events.
        }
        await database.SaveChangesAsync();
        Assert.AreEqual(LiveUpdateTopics.None, (await BarrierAsync(factory, viewer))[0]);
    }

    [TestMethod]
    public async Task DemotedConnectionsLoseAdminUpdatesAndDisabledConnectionsAreClosed()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var email = await CreateUserAsync(factory, admin: true);
        await using var viewer = await Viewer.ConnectAsync(factory, email, WebTestFixture.UserPassword);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(viewer.UserId.ToString());
        Assert.IsNotNull(user);
        Assert.IsTrue((await users.RemoveFromRoleAsync(user, "Admin")).Succeeded);
        await factory.Services.GetRequiredService<LiveUpdatesPublisher>().PublishAsync(
            new LiveChanges { AdminTopics = LiveUpdateTopics.Security | LiveUpdateTopics.Publishing });
        Assert.AreEqual(LiveUpdateTopics.None, (await BarrierAsync(factory, viewer))[0]);

        user.Status = UserStatus.Disabled;
        Assert.IsTrue((await users.UpdateAsync(user)).Succeeded);
        await viewer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HubConnectionState.Disconnected, viewer.Connection.State);
    }

    [TestMethod]
    public async Task CatalogProgressUsesTheSharedHubForAdminsAndReadinessForUsers()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        await using var admin = await Viewer.ConnectAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await using var reader = await Viewer.ConnectAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await database.Set<FamilyLibrarian.Infrastructure.Gutenberg.GutenbergCatalogSyncStateEntity>().SingleOrDefaultAsync();
        if (state is null)
        {
            state = new FamilyLibrarian.Infrastructure.Gutenberg.GutenbergCatalogSyncStateEntity();
            database.Add(state);
        }
        state.Status = $"Download-{Guid.NewGuid():N}"[..17];
        await database.SaveChangesAsync();
        var adminTopics = await admin.NextAsync();
        var readerTopics = await reader.NextAsync();
        Assert.IsTrue(adminTopics.HasFlag(LiveUpdateTopics.Sources | LiveUpdateTopics.System));
        Assert.AreEqual(LiveUpdateTopics.System, readerTopics);
    }

    private static NotificationEvent Notification(Guid? userId) => new(
        userId is null ? NotificationAudience.AdminBroadcast : NotificationAudience.SingleUser,
        userId, "test", NotificationSeverity.Info, "Test update", null, null, null, DateTimeOffset.UtcNow);

    // A shared, ordered marker proves absence of private messages without sleeps.
    private static async Task<LiveUpdateTopics[]> BarrierAsync(FamilyLibrarianAppFactory factory, params Viewer[] viewers)
    {
        await factory.Services.GetRequiredService<LiveUpdatesPublisher>().PublishAsync(
            new LiveChanges { SharedTopics = LiveUpdateTopics.System });
        return await Task.WhenAll(viewers.Select(async viewer =>
        {
            var received = LiveUpdateTopics.None;
            while (true)
            {
                var topics = await viewer.NextAsync();
                received |= topics & ~LiveUpdateTopics.System;
                if (topics.HasFlag(LiveUpdateTopics.System)) return received;
            }
        }));
    }

    private static async Task<string> CreateUserAsync(FamilyLibrarianAppFactory factory, bool admin = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"live-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { UserName = email, Email = email, DisplayName = "Live test", EmailConfirmed = true };
        Assert.IsTrue((await users.CreateAsync(user, WebTestFixture.UserPassword)).Succeeded);
        Assert.IsTrue((await users.AddToRoleAsync(user, admin ? "Admin" : "User")).Succeeded);
        return email;
    }

    private sealed class Viewer(HttpClient http, HubConnection connection, Guid userId) : IAsyncDisposable
    {
        private readonly Channel<LiveUpdateTopics> messages = Channel.CreateUnbounded<LiveUpdateTopics>();
        public HttpClient Http => http;
        public HubConnection Connection => connection;
        public Guid UserId => userId;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Viewer> ConnectAsync(FamilyLibrarianAppFactory factory, string email, string password)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
            Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
            var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));
            var user = await client.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");
            Assert.IsNotNull(user);
            client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
                await WebTestFixture.GetAntiforgeryTokenAsync(client));
            var connection = new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, LiveUpdates.HubPath), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookies;
            }).Build();
            var viewer = new Viewer(client, connection, user.Id);
            connection.On<LiveUpdateTopics>(LiveUpdates.Changed, topics => viewer.messages.Writer.TryWrite(topics));
            connection.Closed += _ => { viewer.Closed.TrySetResult(); return Task.CompletedTask; };
            await connection.StartAsync();
            return viewer;
        }

        public async Task<LiveUpdateTopics> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await messages.Reader.ReadAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            http.Dispose();
        }
    }
}
