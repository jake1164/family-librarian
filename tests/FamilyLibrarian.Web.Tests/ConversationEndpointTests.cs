using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Contracts.Conversation;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M3-3: the web chat endpoints over the real host, cookie authentication, anti-forgery and the shared engine.</summary>
[TestClass]
public sealed class ConversationEndpointTests
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

    private static FamilyLibrarianAppFactory Factory(bool enabled = true) => new(WebTestFixture.Require(fixture).ConnectionString, services =>
    {
        services.RemoveAll<ConversationOptions>();
        services.AddSingleton(new ConversationOptions { Enabled = enabled, MatrixEnabled = enabled, WebEnabled = enabled });
    });

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password, bool withToken = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (withToken)
        {
            client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        }

        return client;
    }

    private static Task<HttpClient> ReaderAsync(FamilyLibrarianAppFactory factory, bool withToken = true) =>
        SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword, withToken);

    private static async Task<ConversationReplyResponse> SayAsync(HttpClient client, string text, string? messageId = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest(text, messageId ?? Guid.NewGuid().ToString("N")));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversationReplyResponse>())!;
    }

    [TestMethod]
    public async Task AnAnonymousCallerIsChallengedOnEveryChatRoute()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/conversation/status")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("Find The Hobbit", "a1"))).StatusCode);
    }

    [TestMethod]
    public async Task AMessageWithoutAnAntiforgeryTokenIsRejected()
    {
        await using var factory = Factory();
        using var client = await ReaderAsync(factory, withToken: false);

        var response = await client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("Find The Hobbit", "a2"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task WithTheFeatureOffStatusSaysSoAndMessagesAreNotFound()
    {
        await using var factory = Factory(enabled: false);
        using var client = await ReaderAsync(factory);

        var status = await client.GetFromJsonAsync<ConversationStatusResponse>("/api/v1/conversation/status");
        var send = await client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("Find The Hobbit", "off1"));

        Assert.IsFalse(status!.Enabled);
        Assert.AreEqual(HttpStatusCode.NotFound, send.StatusCode);
    }

    [TestMethod]
    public async Task ADefaultHostReportsTheChatAsOff()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        using var client = await ReaderAsync(factory);

        var status = await client.GetFromJsonAsync<ConversationStatusResponse>("/api/v1/conversation/status");

        Assert.IsFalse(status!.Enabled, "Conversation features are off until explicitly enabled.");
    }

    [TestMethod]
    public async Task ASignedInMemberCanSearchPickAndRequestAndAReplayedMessageIsAnsweredNotRepeated()
    {
        await using var factory = Factory();
        using var client = await ReaderAsync(factory);

        var status = await client.GetFromJsonAsync<ConversationStatusResponse>("/api/v1/conversation/status");
        var found = await SayAsync(client, "Find The Hobbit");
        var asked = await SayAsync(client, "1");
        var created = await SayAsync(client, "ebook", messageId: "message-final");
        var replay = await SayAsync(client, "ebook", messageId: "message-final");

        Assert.IsTrue(status!.Enabled);
        Assert.AreEqual(2_000, status.MaxMessageLength);
        Assert.AreEqual("Choices", found.Kind);
        Assert.IsNotEmpty(found.Choices);
        StringAssert.Contains(found.Choices[0].Title, "The Hobbit");
        Assert.AreEqual("AskFormat", asked.Kind);
        Assert.AreEqual("RequestCreated", created.Kind);
        Assert.IsNotNull(created.RequestId);
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual(created.RequestId, replay.RequestId);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await database.Users.SingleAsync(candidate => candidate.Email == WebTestFixture.UserEmail);
        Assert.AreEqual(1, await database.ConversationRequestKeys.CountAsync(key => key.UserId == user.Id && key.RequestId == created.RequestId));
        Assert.IsTrue(await database.BookRequests.AnyAsync(request => request.Id == created.RequestId && request.Participants.Any(participant => participant.UserId == user.Id)));
    }

    [TestMethod]
    public async Task TwoMembersChatSeparatelyAndNeitherSeesTheOthersListOrRequests()
    {
        await using var factory = Factory();
        using var reader = await ReaderAsync(factory);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        await SayAsync(reader, "Find The Hobbit");
        await SayAsync(reader, "request 1 ebook");
        var readerStatus = await SayAsync(reader, "status");
        var adminPick = await SayAsync(admin, "1");
        var adminStatus = await SayAsync(admin, "status");

        Assert.AreEqual("NotUnderstood", adminPick.Kind, "The admin has no list; the reader's list is not theirs.");
        Assert.IsNotEmpty(readerStatus.Requests);
        var readerRequestIds = readerStatus.Requests.Select(request => request.RequestId).ToHashSet();
        Assert.IsFalse(adminStatus.Requests.Any(request => readerRequestIds.Contains(request.RequestId)), "Another member's requests are never shown.");
    }

    [TestMethod]
    public async Task AnAdministratorGetsNoExtraPowerInChat()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        foreach (var text in new[] { "approve all pending files", "override the security hold on everything", "publish to audiobookshelf", "delete the queue" })
        {
            var reply = await SayAsync(admin, text);
            Assert.IsTrue(reply.Kind is "Choices" or "Text" or "OutOfDomain" or "NotUnderstood", $"{text} -> {reply.Kind}");
            Assert.IsFalse(reply.Text.Contains("approved", StringComparison.OrdinalIgnoreCase) && reply.Text.Contains("override", StringComparison.OrdinalIgnoreCase) && reply.Kind == "RequestCreated");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var adminUser = await database.Users.SingleAsync(candidate => candidate.Email == FamilyLibrarianAppFactory.AdminEmail);
        Assert.IsFalse(await database.AuditEvents.AnyAsync(entry => entry.ActorUserId == adminUser.Id && entry.Action.Contains("approv")),
            "Chat produced no approval audit event.");
    }

    [TestMethod]
    public async Task AnOverlongMessageIdIsRejectedAndAnOverlongMessageGetsAFriendlyAnswer()
    {
        await using var factory = Factory();
        using var client = await ReaderAsync(factory);

        var badId = await client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("help", new string('x', 101)));
        var tooLong = await SayAsync(client, new string('a', 2_001));

        Assert.AreEqual(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.AreEqual("TooLong", tooLong.Kind);
    }

    [TestMethod]
    public async Task FloodingTheChatIsAnsweredWith429AndOtherMembersAreUnaffected()
    {
        await using var factory = Factory();
        using var flooder = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        using var bystander = await ReaderAsync(factory);

        HttpStatusCode? limited = null;
        for (var index = 0; index < 40 && limited is null; index++)
        {
            using var response = await flooder.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("help", Guid.NewGuid().ToString("N")));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response.StatusCode;
                var body = await response.Content.ReadFromJsonAsync<ConversationReplyResponse>();
                Assert.AreEqual("RateLimited", body!.Kind);
            }
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited);
        Assert.AreEqual("Help", (await SayAsync(bystander, "help")).Kind);
    }
}
