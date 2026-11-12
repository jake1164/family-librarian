using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Conversation;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// M3-4: the same conversation script, run once through the web endpoint and once through Matrix, produces the same answers
/// and the same domain effects. The transports differ only in how identity arrives and how text is presented.
/// </summary>
[TestClass]
public sealed class ConversationParityTests
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

    private static readonly string[] Script =
    [
        "Find Project Hail Mary",
        "details 1",
        "what's the weather?",
        "request 1 audiobook",
        "status",
        "Do we have Dune?",
        "help",
        "cancel",
        "7"
    ];

    private sealed class ScriptedServer : IMatrixClient
    {
        private string token = "t0";
        private List<MatrixInboundMessage> batch = [];

        public List<(string Room, string Text)> Sent { get; } = [];

        public void Deliver(MatrixInboundMessage message)
        {
            token = $"t{Guid.NewGuid():N}";
            batch = [message];
        }

        public Task<MatrixSyncResult> SyncAsync(MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixSyncResult.Success(token, since == token ? [] : batch));

        public Task<SendResult> SendMessageAsync(MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
        {
            Sent.Add((roomId, text));
            return Task.FromResult(SendResult.Success());
        }

        public Task<FamilyLibrarian.Application.Publishing.ConnectionTestOutcome> TestConnectionAsync(MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixSendResult> SendRichMessageAsync(MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SendResult> EditMessageAsync(MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody, string htmlBody, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record Rig(FamilyLibrarianAppFactory Factory, ScriptedServer Server);

    private static async Task<Rig> CreateRigAsync()
    {
        var server = new ScriptedServer();
        var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<ConversationOptions>();
            services.AddSingleton(new ConversationOptions { Enabled = true, MatrixEnabled = true, WebEnabled = true, MaxMessagesPerMinute = 200 });
            services.RemoveAll<IMatrixClient>();
            services.AddSingleton<IMatrixClient>(server);
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
        var now = DateTimeOffset.UtcNow;
        var settings = new MatrixSettings(now);
        settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, now);
        settings.SetAccessToken(protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "token"), protector.FormatVersion, actorUserId: null, now);
        settings.SetEnabled(true, actorUserId: null, now);
        database.MatrixSettings.Add(settings);
        await database.SaveChangesAsync();
        return new Rig(factory, server);
    }

    private static async Task<HttpClient> WebClientAsync(FamilyLibrarianAppFactory factory, string email)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static async Task<List<(string Kind, string Text)>> RunWebAsync(HttpClient client, IEnumerable<string> script)
    {
        var answers = new List<(string, string)>();
        foreach (var text in script)
        {
            using var response = await client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest(text, Guid.NewGuid().ToString("N")));
            response.EnsureSuccessStatusCode();
            var reply = (await response.Content.ReadFromJsonAsync<ConversationReplyResponse>())!;
            answers.Add((reply.Kind, reply.Text));
        }

        return answers;
    }

    private static async Task<List<(string Kind, string Text)>> RunMatrixAsync(Rig rig, ConversationHostTests.LinkedUser user, IEnumerable<string> script)
    {
        var answers = new List<(string, string)>();
        var counter = 0;
        foreach (var text in script)
        {
            var before = rig.Server.Sent.Count;
            rig.Server.Deliver(new MatrixInboundMessage(user.Room, user.MatrixId, text, EventId: $"${user.UserId:N}-{++counter}"));
            await using var scope = rig.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatrixInboundSyncCoordinator>().PollOnceAsync(CancellationToken.None);
            var sent = rig.Server.Sent.Skip(before).Select(entry => entry.Text).ToArray();
            Assert.HasCount(1, sent, $"'{text}' got exactly one reply over Matrix.");
            answers.Add(("sent", sent[0]));
        }

        return answers;
    }

    private static async Task<List<(string Title, string Formats)>> OwnRequestsAsync(FamilyLibrarianAppFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requests = await database.BookRequests.Include(request => request.Formats)
            .Where(request => request.Participants.Any(participant => participant.UserId == userId)).ToListAsync();
        var titles = await database.Works.Where(work => requests.Select(request => request.WorkId).Contains(work.Id))
            .ToDictionaryAsync(work => work.Id, work => work.CanonicalTitle);
        return [.. requests.Select(request => (titles[request.WorkId], string.Join(",", request.Formats.Select(format => format.MediaType).Order())))];
    }

    [TestMethod]
    public async Task TheSameScriptGivesTheSameAnswersAndTheSameEffectsOverWebAndMatrix()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var webUser = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, "Web Reader");
        var matrixUser = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, "Matrix Reader");
        using var web = await WebClientAsync(rig.Factory, webUser.Email);

        var webAnswers = await RunWebAsync(web, Script);
        var matrixAnswers = await RunMatrixAsync(rig, matrixUser, Script);

        Assert.HasCount(Script.Length, webAnswers);
        for (var index = 0; index < Script.Length; index++)
        {
            Assert.AreEqual(webAnswers[index].Text, matrixAnswers[index].Text, $"Step {index + 1} '{Script[index]}' reads the same on both transports.");
        }

        CollectionAssert.AreEqual(
            await OwnRequestsAsync(rig.Factory, webUser.UserId), await OwnRequestsAsync(rig.Factory, matrixUser.UserId),
            "Each user ends with the same request: one Project Hail Mary audiobook.");
        var web1 = await OwnRequestsAsync(rig.Factory, webUser.UserId);
        Assert.AreEqual(1, web1.Count(request => request.Title == "Project Hail Mary"));
        Assert.AreEqual("Audiobook", web1.Single(request => request.Title == "Project Hail Mary").Formats);
        StringAssert.Contains(webAnswers[2].Text, "only help with books");
        Assert.AreEqual("Requested", webAnswers[3].Text[..9]);
    }

    [TestMethod]
    public async Task ReplayingTheRequestMessageIsHarmlessOnBothTransports()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var webUser = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, "Web Replayer");
        var matrixUser = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, "Matrix Replayer");
        using var web = await WebClientAsync(rig.Factory, webUser.Email);

        await RunWebAsync(web, ["Find The Hobbit"]);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await web.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("request 1 ebook", "the-same-id"));
            response.EnsureSuccessStatusCode();
        }

        await RunMatrixAsync(rig, matrixUser, ["Find The Hobbit"]);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            rig.Server.Deliver(new MatrixInboundMessage(matrixUser.Room, matrixUser.MatrixId, "request 1 ebook", EventId: "$the-same-event"));
            await using var scope = rig.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatrixInboundSyncCoordinator>().PollOnceAsync(CancellationToken.None);
        }

        Assert.AreEqual(1, (await OwnRequestsAsync(rig.Factory, webUser.UserId)).Count(request => request.Title == "The Hobbit"));
        Assert.AreEqual(1, (await OwnRequestsAsync(rig.Factory, matrixUser.UserId)).Count(request => request.Title == "The Hobbit"));
    }

    [TestMethod]
    public async Task AccountsThatAreNoLongerActiveAreRefusedOnBothTransports()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var webUser = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, "Web Leaver");
        var matrixUser = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, "Matrix Leaver");
        using var web = await WebClientAsync(rig.Factory, webUser.Email);
        await using (var scope = rig.Factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Accounts.IUserAccountStore>();
            Assert.IsTrue((await accounts.SetStatusAsync(webUser.UserId, FamilyLibrarian.Domain.Accounts.UserStatus.Disabled, CancellationToken.None)).Succeeded);
            Assert.IsTrue((await accounts.SetStatusAsync(matrixUser.UserId, FamilyLibrarian.Domain.Accounts.UserStatus.Disabled, CancellationToken.None)).Succeeded);
        }

        using var webAttempt = await web.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("request 1 ebook", "dis-1"));
        rig.Server.Deliver(new MatrixInboundMessage(matrixUser.Room, matrixUser.MatrixId, "Find The Hobbit", EventId: "$dis-1"));
        await using (var scope = rig.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MatrixInboundSyncCoordinator>().PollOnceAsync(CancellationToken.None);
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, webAttempt.StatusCode, "A disabled member's session no longer works.");
        Assert.IsEmpty(rig.Server.Sent.Where(entry => entry.Room == matrixUser.Room));
        Assert.IsEmpty(await OwnRequestsAsync(rig.Factory, webUser.UserId));
        Assert.IsEmpty(await OwnRequestsAsync(rig.Factory, matrixUser.UserId));
    }

    [TestMethod]
    public async Task ManyMembersAcrossBothTransportsAtOnceEachGetOnlyTheirOwnRequest()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var webUsers = new List<ConversationHostTests.LinkedUser>();
        var matrixUsers = new List<ConversationHostTests.LinkedUser>();
        for (var index = 0; index < 4; index++)
        {
            webUsers.Add(await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, $"Web {index}"));
            matrixUsers.Add(await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, $"Matrix {index}"));
        }

        var clients = new List<HttpClient>();
        foreach (var user in webUsers) clients.Add(await WebClientAsync(rig.Factory, user.Email));
        using var start = new ManualResetEventSlim();
        var work = new List<Task>();
        for (var index = 0; index < clients.Count; index++)
        {
            var client = clients[index];
            work.Add(Task.Run(async () =>
            {
                start.Wait();
                await RunWebAsync(client, ["Find A Wrinkle in Time", "request 1 ebook"]);
            }));
        }

        foreach (var user in matrixUsers)
        {
            work.Add(Task.Run(async () =>
            {
                start.Wait();
                for (var step = 0; step < 2; step++)
                {
                    await using var scope = rig.Factory.Services.CreateAsyncScope();
                    var turn = await scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>()
                        .AuthenticateAsync(new MatrixInboundMessage(user.Room, user.MatrixId, "x"), CancellationToken.None);
                    Assert.IsTrue(turn.IsAuthenticated);
                    var text = step == 0 ? "Find A Wrinkle in Time" : "request 1 ebook";
                    await scope.ServiceProvider.GetRequiredService<IConversationService>().HandleAsync(
                        new ConversationTurn(ConversationTransports.Matrix, user.Room, $"$conc-{user.UserId:N}-{step}", text), CancellationToken.None);
                }
            }));
        }

        start.Set();
        await Task.WhenAll(work);
        foreach (var client in clients) client.Dispose();

        foreach (var user in webUsers.Concat(matrixUsers))
        {
            var own = await OwnRequestsAsync(rig.Factory, user.UserId);
            Assert.AreEqual(1, own.Count(request => request.Title == "A Wrinkle in Time"), $"{user.UserId} has exactly one request.");
        }
    }
}
