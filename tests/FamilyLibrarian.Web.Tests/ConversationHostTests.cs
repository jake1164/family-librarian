using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The conversation engine wired into the real host and database: real catalog search, the real work resolver and the
/// unchanged <c>BookRequestService</c>, driven as a Matrix-derived principal.
/// </summary>
[TestClass]
public sealed class ConversationHostTests
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

    internal sealed record LinkedUser(Guid UserId, string Room, string MatrixId);

    private static FamilyLibrarianAppFactory Factory(bool enabled = true) => new(WebTestFixture.Require(fixture).ConnectionString, services =>
    {
        services.RemoveAll<ConversationOptions>();
        services.AddSingleton(new ConversationOptions { Enabled = enabled, MatrixEnabled = enabled, WebEnabled = enabled });
    });

    internal static async Task<LinkedUser> CreateLinkedUserAsync(FamilyLibrarianAppFactory factory, string displayName = "Mara Quill")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"chat-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true, Status = UserStatus.Active };
        Assert.IsTrue((await users.CreateAsync(user, WebTestFixture.UserPassword)).Succeeded);
        Assert.IsTrue((await users.AddToRoleAsync(user, "User")).Succeeded);
        var room = $"!{Guid.NewGuid():N}:example.test";
        var matrixId = $"@{user.Id:N}:example.test";
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var destination = new UserMatrixDestination(user.Id, DateTimeOffset.UtcNow);
        destination.RequestVerification(matrixId, room, "000000", DateTimeOffset.UtcNow);
        destination.Verify(DateTimeOffset.UtcNow);
        database.UserMatrixDestinations.Add(destination);
        await database.SaveChangesAsync();
        return new LinkedUser(user.Id, room, matrixId);
    }

    /// <summary>One message in its own scope, authenticated from the Matrix link exactly as an inbound event would be.</summary>
    internal static async Task<ConversationTurnResult> SayAsMatrixAsync(FamilyLibrarianAppFactory factory, LinkedUser user, string text, string eventId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var authenticated = await scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>()
            .AuthenticateAsync(new MatrixInboundMessage(user.Room, user.MatrixId, text), CancellationToken.None);
        Assert.IsTrue(authenticated.IsAuthenticated);
        return await scope.ServiceProvider.GetRequiredService<IConversationService>()
            .HandleAsync(new ConversationTurn(ConversationTransports.Matrix, user.Room, eventId, text), CancellationToken.None);
    }

    [TestMethod]
    public async Task ASearchPickAndFormatCreateOneOrdinaryRequestAndAReplayCreatesNoSecondOne()
    {
        await using var factory = Factory();
        var user = await CreateLinkedUserAsync(factory);

        var found = await SayAsMatrixAsync(factory, user, "Find The Hobbit", "$e1");
        var asked = await SayAsMatrixAsync(factory, user, "1", "$e2");
        var created = await SayAsMatrixAsync(factory, user, "ebook", "$e3");
        var replay = await SayAsMatrixAsync(factory, user, "ebook", "$e3");

        Assert.AreEqual(ConversationReplyKind.Choices, found.Reply.Kind);
        StringAssert.Contains(found.Reply.Text, "The Hobbit");
        Assert.AreEqual(ConversationReplyKind.AskFormat, asked.Reply.Kind);
        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual(created.Reply.RequestId, replay.Reply.RequestId);
        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var requests = await database.BookRequests.Include(request => request.Participants)
            .Where(request => request.Participants.Any(participant => participant.UserId == user.UserId)).ToListAsync();
        Assert.HasCount(1, requests, "Exactly one request, owned by the verified account.");
        Assert.AreEqual(created.Reply.RequestId, requests[0].Id);
        Assert.AreEqual(1, await database.ConversationRequestKeys.CountAsync(key => key.UserId == user.UserId));
    }

    [TestMethod]
    public async Task StatusShowsTheRequestAndAskingForTheSameBookAgainReusesIt()
    {
        await using var factory = Factory();
        var user = await CreateLinkedUserAsync(factory);
        await SayAsMatrixAsync(factory, user, "Find Project Hail Mary", "$a1");
        var firstRequest = await SayAsMatrixAsync(factory, user, "request 1 ebook", "$a2");

        var status = await SayAsMatrixAsync(factory, user, "is my request ready?", "$a3");
        await SayAsMatrixAsync(factory, user, "Find Project Hail Mary", "$a4");
        var again = await SayAsMatrixAsync(factory, user, "request 1 ebook", "$a5");

        Assert.AreEqual(ConversationReplyKind.Status, status.Reply.Kind);
        Assert.IsTrue(status.Reply.Requests.Any(request => request.Title.Contains("Project Hail Mary", StringComparison.Ordinal)));
        Assert.AreEqual(firstRequest.Reply.RequestId, again.Reply.RequestId, "BookRequestService joins the existing request instead of creating a second.");
        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.BookRequests.CountAsync(request => request.Participants.Any(participant => participant.UserId == user.UserId)));
    }

    [TestMethod]
    public async Task OneUsersStatusNeverIncludesAnotherUsersRequests()
    {
        await using var factory = Factory();
        var first = await CreateLinkedUserAsync(factory, "First Reader");
        var second = await CreateLinkedUserAsync(factory, "Second Reader");
        await SayAsMatrixAsync(factory, first, "Find The Hobbit", "$f1");
        await SayAsMatrixAsync(factory, first, "request 1 ebook", "$f2");

        var firstStatus = await SayAsMatrixAsync(factory, first, "status", "$f3");
        var secondStatus = await SayAsMatrixAsync(factory, second, "status", "$s1");

        Assert.IsNotEmpty(firstStatus.Reply.Requests);
        Assert.IsEmpty(secondStatus.Reply.Requests);
        StringAssert.Contains(secondStatus.Reply.Text, "don't have any requests");
    }

    [TestMethod]
    public async Task WithTheFeatureOffNothingIsSearchedOrStored()
    {
        await using var factory = Factory(enabled: false);
        var user = await CreateLinkedUserAsync(factory);

        var result = await SayAsMatrixAsync(factory, user, "Find The Hobbit", "$off1");

        Assert.AreEqual(ConversationReplyKind.Disabled, result.Reply.Kind);
        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.IsFalse(await database.ConversationSessions.AnyAsync(session => session.UserId == user.UserId));
        Assert.IsFalse(await database.ProcessedConversationEvents.AnyAsync(item => item.UserId == user.UserId));
    }

    [TestMethod]
    public async Task ADefaultHostHasTheConversationFlagsOff()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var options = scope.ServiceProvider.GetRequiredService<ConversationOptions>();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.MatrixEnabled);
        Assert.IsFalse(options.WebEnabled);
    }
}
