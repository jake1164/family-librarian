using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// M2-1 against the real host and database: the existing request and audit services run unchanged under a Matrix-derived
/// principal, attribute work to the verified account, and stop the moment the account, link or role changes.
/// </summary>
[TestClass]
public sealed class MatrixPrincipalHostTests
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

    private sealed record Linked(Guid UserId, string Room, string MatrixId);

    private static async Task<Linked> CreateLinkedUserAsync(FamilyLibrarianAppFactory factory, bool admin = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"matrix-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { UserName = email, Email = email, DisplayName = "Mara Quill", EmailConfirmed = true, Status = UserStatus.Active };
        Assert.IsTrue((await users.CreateAsync(user, WebTestFixture.UserPassword)).Succeeded);
        Assert.IsTrue((await users.AddToRoleAsync(user, "User")).Succeeded);
        if (admin) Assert.IsTrue((await users.AddToRoleAsync(user, "Admin")).Succeeded);

        var room = $"!{Guid.NewGuid():N}:example.test";
        var matrixId = $"@{user.Id:N}:example.test";
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var destination = new UserMatrixDestination(user.Id, DateTimeOffset.UtcNow);
        destination.RequestVerification(matrixId, room, "000000", DateTimeOffset.UtcNow);
        destination.Verify(DateTimeOffset.UtcNow);
        database.UserMatrixDestinations.Add(destination);
        await database.SaveChangesAsync();
        return new Linked(user.Id, room, matrixId);
    }

    private static async Task<MatrixAuthenticationResult> AuthenticateAsync(AsyncServiceScope scope, Linked link, string? sender = null) =>
        await scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>()
            .AuthenticateAsync(new MatrixInboundMessage(link.Room, sender ?? link.MatrixId, "hi"), CancellationToken.None);

    private static async Task<Guid> ResolveWorkAsync(FamilyLibrarianAppFactory factory)
    {
        using var client = await WebTestFixture.Require(fixture).CreateUserClientAsync();
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        var resolved = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", null);
        resolved.EnsureSuccessStatusCode();
        return (await resolved.Content.ReadFromJsonAsync<CatalogWorkResponse>())!.Id;
    }

    [TestMethod]
    public async Task TheUnchangedRequestServiceCreatesTheRequestForTheVerifiedAccountAndAuditAttributesIt()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var link = await CreateLinkedUserAsync(factory);
        var workId = await ResolveWorkAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.IsTrue((await AuthenticateAsync(scope, link)).IsAuthenticated);
            var created = await scope.ServiceProvider.GetRequiredService<BookRequestService>()
                .CreateAsync(workId, [RequestMediaType.Ebook], null, false, false, CancellationToken.None);
            Assert.AreEqual(CreateBookRequestOutcome.Created, created.Outcome);

            await scope.ServiceProvider.GetRequiredService<IAuditWriter>()
                .WriteAsync("matrix.principal.test", "test", link.UserId.ToString(), null, CancellationToken.None);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await database.BookRequests.Include(candidate => candidate.Participants)
            .SingleAsync(candidate => candidate.WorkId == workId && candidate.Participants.Any(participant => participant.UserId == link.UserId));
        Assert.IsTrue(request.ActiveRequesterIds.Contains(link.UserId));
        var audit = await database.AuditEvents.SingleAsync(entry => entry.Action == "matrix.principal.test" && entry.SubjectId == link.UserId.ToString());
        Assert.AreEqual(link.UserId, audit.ActorUserId);
        Assert.AreEqual("Mara Quill", audit.ActorDisplayName, "The account's stored name, not anything the sender supplied.");
    }

    [TestMethod]
    public async Task AScopeThatWasNeverAuthenticatedCreatesNothingAndAuditsNoUser()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var linked = await CreateLinkedUserAsync(factory);
        var workId = await ResolveWorkAsync(factory);
        await using (var other = factory.Services.CreateAsyncScope())
        {
            Assert.IsTrue((await AuthenticateAsync(other, linked)).IsAuthenticated);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<BookRequestService>()
            .CreateAsync(workId, [RequestMediaType.Audiobook], null, false, false, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>()
            .WriteAsync("matrix.unauthenticated.test", "test", null, null, CancellationToken.None);

        Assert.AreEqual(CreateBookRequestOutcome.Unauthenticated, created.Outcome);
        Assert.IsNull(scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.IsNull((await database.AuditEvents.SingleAsync(entry => entry.Action == "matrix.unauthenticated.test")).ActorUserId);
        Assert.IsFalse(await database.BookRequests.AnyAsync(request =>
            request.WorkId == workId && request.Participants.Any(participant => participant.UserId == linked.UserId)),
            "Another scope's earlier authentication left nothing behind.");
    }

    [TestMethod]
    public async Task AWrongSenderInTheLinkedRoomCreatesNothing()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var link = await CreateLinkedUserAsync(factory);
        var workId = await ResolveWorkAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var result = await AuthenticateAsync(scope, link, sender: "@intruder:example.test");
        var created = await scope.ServiceProvider.GetRequiredService<BookRequestService>()
            .CreateAsync(workId, [RequestMediaType.Ebook], null, false, false, CancellationToken.None);

        Assert.AreEqual(MatrixAuthenticationOutcome.SenderMismatch, result.Outcome);
        Assert.AreEqual(CreateBookRequestOutcome.Unauthenticated, created.Outcome);
    }

    [TestMethod]
    public async Task DisablingDowngradingAndUnlinkingAreHonoredByTheNextOperationInTheRealDatabase()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var admin = await CreateLinkedUserAsync(factory, admin: true);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.IsTrue((await AuthenticateAsync(scope, admin)).IsAuthenticated);
            Assert.IsTrue(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountStore>().SetAdminAsync(admin.UserId, false, CancellationToken.None);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.IsTrue((await AuthenticateAsync(scope, admin)).IsAuthenticated);
            Assert.IsFalse(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator, "Downgraded: no retained privilege.");
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountStore>().SetStatusAsync(admin.UserId, UserStatus.Disabled, CancellationToken.None);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await AuthenticateAsync(scope, admin);
            Assert.AreEqual(MatrixAuthenticationOutcome.AccountNotActive, result.Outcome);
            Assert.IsNull(scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
        }

        var member = await CreateLinkedUserAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await database.UserMatrixDestinations.SingleAsync(destination => destination.UserId == member.UserId)).Unlink(DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await AuthenticateAsync(scope, member);
            Assert.IsFalse(result.IsAuthenticated, "A revoked link stops resolving to anyone.");
        }
    }

    [TestMethod]
    public async Task ADeletedAccountCannotAuthenticate()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var link = await CreateLinkedUserAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.UserMatrixDestinations.Where(destination => destination.UserId == link.UserId).ExecuteDeleteAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            Assert.IsTrue((await users.DeleteAsync((await users.FindByIdAsync(link.UserId.ToString()))!)).Succeeded);
        }

        await using var check = factory.Services.CreateAsyncScope();
        Assert.IsFalse((await AuthenticateAsync(check, link)).IsAuthenticated);
        Assert.IsNull(check.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
    }

    [TestMethod]
    public async Task ConcurrentOperationsForDifferentUsersAttributeAuditToTheirOwnAccount()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        var people = new List<Linked>();
        for (var index = 0; index < 8; index++) people.Add(await CreateLinkedUserAsync(factory));
        var action = $"matrix.concurrent.{Guid.NewGuid():N}";
        using var start = new ManualResetEventSlim();

        var running = people.Select(person => Task.Run(async () =>
        {
            start.Wait();
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.IsTrue((await AuthenticateAsync(scope, person)).IsAuthenticated);
            await Task.Yield();
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>()
                .WriteAsync(action, "test", person.UserId.ToString(), null, CancellationToken.None);
        })).ToArray();
        start.Set();
        await Task.WhenAll(running);

        await using var check = factory.Services.CreateAsyncScope();
        var events = await check.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents
            .Where(entry => entry.Action == action).ToListAsync();
        Assert.HasCount(people.Count, events);
        Assert.IsTrue(events.All(entry => entry.SubjectId == entry.ActorUserId.ToString()), "Each event was attributed to its own user.");
    }
}
