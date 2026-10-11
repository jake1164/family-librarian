using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// M3-1/M3-2 through the real sync coordinator and router: a scripted homeserver delivers events, the real router and
/// conversation engine act on the real database, and the bot's replies are captured. Existing commands keep priority.
/// </summary>
[TestClass]
public sealed class MatrixConversationHostTests
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

    private sealed class Server : IMatrixClient
    {
        private string token = "t0";
        private List<MatrixInboundMessage> batch = [];

        public List<(string Room, string Text)> Sent { get; } = [];

        public int SyncCalls { get; private set; }

        /// <summary>Events the homeserver will deliver to any client that has not yet seen the returned token.</summary>
        public void Deliver(params MatrixInboundMessage[] events)
        {
            token = $"t{Guid.NewGuid():N}";
            batch = [.. events];
        }

        public IEnumerable<string> RepliesTo(string room) => Sent.Where(entry => entry.Room == room).Select(entry => entry.Text);

        public Task<MatrixSyncResult> SyncAsync(MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken)
        {
            SyncCalls++;
            return Task.FromResult(MatrixSyncResult.Success(token, since == token ? [] : batch));
        }

        public Task<SendResult> SendMessageAsync(MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
        {
            Sent.Add((roomId, text));
            return Task.FromResult(SendResult.Success());
        }

        public Task<ConnectionTestOutcome> TestConnectionAsync(MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixSendResult> SendRichMessageAsync(MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SendResult> EditMessageAsync(MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody, string htmlBody, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Fails the cursor save once, as a crash between processing messages and remembering where we were.</summary>
    private sealed class FaultOnceSettingsStore(IMatrixSettingsStore inner, FaultSwitch fault) : IMatrixSettingsStore
    {
        public Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken) => inner.FindAsync(cancellationToken);

        public Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken) => inner.GetOrCreateAsync(cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (fault.Armed)
            {
                fault.Armed = false;
                throw new InvalidOperationException("simulated crash before the sync cursor was saved");
            }

            return inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FaultSwitch
    {
        public bool Armed { get; set; }
    }

    private sealed record Rig(FamilyLibrarianAppFactory Factory, Server Server, FaultSwitch Fault);

    private static async Task<Rig> CreateRigAsync(bool conversation = true)
    {
        var server = new Server();
        var fault = new FaultSwitch();
        var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<ConversationOptions>();
            services.AddSingleton(new ConversationOptions { Enabled = conversation, MatrixEnabled = conversation, WebEnabled = conversation });
            services.RemoveAll<IMatrixClient>();
            services.AddSingleton<IMatrixClient>(server);
            services.RemoveAll<IMatrixSettingsStore>();
            services.AddScoped<IMatrixSettingsStore>(provider =>
                new FaultOnceSettingsStore(ActivatorUtilities.CreateInstance<MatrixSettingsStore>(provider), fault));
        });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
            var now = DateTimeOffset.UtcNow;
            var settings = new MatrixSettings(now);
            settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, now);
            settings.SetAccessToken(protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "test-access-token"), protector.FormatVersion, actorUserId: null, now);
            settings.SetEnabled(true, actorUserId: null, now);
            database.MatrixSettings.Add(settings);
            await database.SaveChangesAsync();
        }

        return new Rig(factory, server, fault);
    }

    private static async Task<int> PollAsync(Rig rig)
    {
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MatrixInboundSyncCoordinator>().PollOnceAsync(CancellationToken.None);
    }

    private static MatrixInboundMessage Say(ConversationHostTests.LinkedUser user, string text, string eventId, string? sender = null) =>
        new(user.Room, sender ?? user.MatrixId, text, EventId: eventId);

    private static async Task<int> RequestCountAsync(FamilyLibrarianAppFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().BookRequests
            .CountAsync(request => request.Participants.Any(participant => participant.UserId == userId));
    }

    [TestMethod]
    public async Task ARequestAndStatusConversationWorksOverMatrixAndRepliesAreDelivered()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);

        rig.Server.Deliver(Say(user, "Find The Hobbit", "$m1"));
        await PollAsync(rig);
        rig.Server.Deliver(Say(user, "request 1 ebook", "$m2"));
        await PollAsync(rig);
        rig.Server.Deliver(Say(user, "status", "$m3"));
        await PollAsync(rig);

        var replies = rig.Server.RepliesTo(user.Room).ToArray();
        Assert.HasCount(3, replies);
        StringAssert.Contains(replies[0], "The Hobbit");
        StringAssert.Contains(replies[1], "Requested");
        StringAssert.Contains(replies[2], "Your requests");
        Assert.AreEqual(1, await RequestCountAsync(rig.Factory, user.UserId));
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var delivered = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessedConversationEvents
            .Where(item => item.UserId == user.UserId).ToListAsync();
        Assert.HasCount(3, delivered);
        Assert.IsTrue(delivered.All(item => item.DeliveredAtUtc is not null), "Every reply was confirmed delivered.");
    }

    [TestMethod]
    public async Task ACrashBeforeTheSyncCursorIsSavedReplaysTheBatchWithoutASecondRequestOrReply()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);
        rig.Server.Deliver(Say(user, "Find The Hobbit", "$c1"));
        await PollAsync(rig);
        rig.Server.Deliver(Say(user, "request 1 ebook", "$c2"));
        rig.Fault.Armed = true;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PollAsync(rig));
        var afterCrash = await RequestCountAsync(rig.Factory, user.UserId);
        var repliesAfterCrash = rig.Server.RepliesTo(user.Room).Count();
        await PollAsync(rig); // the homeserver redelivers the batch because the cursor was never saved

        Assert.AreEqual(1, afterCrash, "The effect was committed before the crash.");
        Assert.AreEqual(1, await RequestCountAsync(rig.Factory, user.UserId), "The replay created nothing more.");
        Assert.AreEqual(repliesAfterCrash, rig.Server.RepliesTo(user.Room).Count(), "The delivered reply was not sent twice.");
    }

    [TestMethod]
    public async Task AReplyThatWasNeverDeliveredIsSentAgainWhenTheEventReplays()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);
        rig.Server.Deliver(Say(user, "Find The Hobbit", "$d1"));
        // The turn commits, then the process dies before the reply is sent and before the cursor is saved.
        await using (var scope = rig.Factory.Services.CreateAsyncScope())
        {
            var authenticated = await scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>()
                .AuthenticateAsync(Say(user, "Find The Hobbit", "$d1"), CancellationToken.None);
            Assert.IsTrue(authenticated.IsAuthenticated);
            await scope.ServiceProvider.GetRequiredService<IConversationService>().HandleAsync(
                new ConversationTurn(ConversationTransports.Matrix, user.Room, "$d1", "Find The Hobbit"), CancellationToken.None);
        }

        await PollAsync(rig);

        var replies = rig.Server.RepliesTo(user.Room).ToArray();
        Assert.HasCount(1, replies, "The recorded reply went out once, from the stored answer.");
        StringAssert.Contains(replies[0], "The Hobbit");
    }

    [TestMethod]
    public async Task ExistingCommandsKeepPriorityOverConversation()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);

        rig.Server.Deliver(Say(user, "yes", "$p1"));
        await PollAsync(rig);

        var reply = rig.Server.RepliesTo(user.Room).Single();
        StringAssert.Contains(reply, "nothing waiting on a confirmation");
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessedConversationEvents.AnyAsync(item => item.UserId == user.UserId),
            "yes/no never enters the conversation.");
    }

    [TestMethod]
    public async Task VerificationStillWorksWithConversationOnAndOnlyForTheRequestedMatrixAccount()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);
        await using (var scope = rig.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var destination = await database.UserMatrixDestinations.SingleAsync(item => item.UserId == user.UserId);
            destination.RequestVerification(user.MatrixId, user.Room, "424242", DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        rig.Server.Deliver(Say(user, "424242", "$v1", sender: "@intruder:example.test"));
        await PollAsync(rig);
        Assert.IsEmpty(rig.Server.RepliesTo(user.Room), "A stranger's guess gets no answer and verifies nothing.");
        rig.Server.Deliver(Say(user, "424242", "$v2"));
        await PollAsync(rig);

        StringAssert.Contains(rig.Server.RepliesTo(user.Room).Single(), "linked");
        await using var check = rig.Factory.Services.CreateAsyncScope();
        Assert.IsTrue((await check.ServiceProvider.GetRequiredService<AppDbContext>().UserMatrixDestinations.SingleAsync(item => item.UserId == user.UserId)).IsVerified);
    }

    [TestMethod]
    public async Task AnotherSenderInTheLinkedRoomCannotUseTheLinkedMembersAuthority()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);

        rig.Server.Deliver(Say(user, "Find The Hobbit", "$s1", sender: "@intruder:example.test"));
        await PollAsync(rig);
        rig.Server.Deliver(Say(user, "request 1 ebook", "$s2", sender: "@intruder:example.test"));
        await PollAsync(rig);
        rig.Server.Deliver(Say(user, "yes", "$s3", sender: "@intruder:example.test"));
        await PollAsync(rig);

        Assert.IsEmpty(rig.Server.RepliesTo(user.Room));
        Assert.AreEqual(0, await RequestCountAsync(rig.Factory, user.UserId));
    }

    [TestMethod]
    public async Task WithConversationOffTheOldBehaviorIsUnchanged()
    {
        var rig = await CreateRigAsync(conversation: false);
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);

        rig.Server.Deliver(Say(user, "Find The Hobbit", "$o1"));
        await PollAsync(rig);

        StringAssert.Contains(rig.Server.RepliesTo(user.Room).Single(), "I didn't understand that");
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.IsFalse(await database.ConversationSessions.AnyAsync(session => session.UserId == user.UserId));
    }

    [TestMethod]
    public async Task UnknownRoomsUnverifiedLinksAndInactiveAccountsGetNoConversation()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var disabled = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);
        var unverified = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);
        await using (var scope = rig.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.UserMatrixDestinations.Where(item => item.UserId == unverified.UserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.VerifiedAtUtc, (DateTimeOffset?)null));
            await scope.ServiceProvider.GetRequiredService<IUserAccountStore>().SetStatusAsync(disabled.UserId, UserStatus.Disabled, CancellationToken.None);
        }

        rig.Server.Deliver(
            new MatrixInboundMessage("!stranger:example.test", "@someone:example.test", "Find The Hobbit", EventId: "$u1"),
            Say(disabled, "Find The Hobbit", "$u2"),
            Say(unverified, "Find The Hobbit", "$u3", sender: unverified.MatrixId));
        await PollAsync(rig);

        Assert.IsFalse(rig.Server.RepliesTo(disabled.Room).Any(text => text.Contains("The Hobbit", StringComparison.Ordinal)), "A disabled account gets no search.");
        Assert.IsFalse(rig.Server.RepliesTo(unverified.Room).Any(text => text.Contains("The Hobbit", StringComparison.Ordinal)));
        Assert.IsFalse(rig.Server.Sent.Any(entry => entry.Room == "!stranger:example.test"));
    }

    [TestMethod]
    public async Task InterleavedMessagesFromDifferentUsersEachActOnlyForTheirOwnAccount()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var users = new List<ConversationHostTests.LinkedUser>();
        for (var index = 0; index < 4; index++) users.Add(await ConversationHostTests.CreateLinkedUserAsync(rig.Factory, $"Reader {index}"));

        rig.Server.Deliver([.. users.Select((user, index) => Say(user, "Find The Hobbit", $"$i1-{index}"))]);
        await PollAsync(rig);
        rig.Server.Deliver([.. users.Select((user, index) => Say(user, "request 1 ebook", $"$i2-{index}"))]);
        await PollAsync(rig);

        foreach (var user in users)
        {
            Assert.AreEqual(1, await RequestCountAsync(rig.Factory, user.UserId));
            Assert.HasCount(2, rig.Server.RepliesTo(user.Room));
        }

        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = users.Select(user => user.UserId).ToArray();
        var requests = await database.BookRequests.Include(request => request.Participants).Where(request => request.Participants.Any(p => ids.Contains(p.UserId))).ToListAsync();
        Assert.AreEqual(1, requests.Count, "All four share the one request for the same book (it joins, never duplicates).");
        Assert.IsTrue(ids.All(id => requests[0].Participants.Any(participant => participant.UserId == id)));
    }

    [TestMethod]
    public async Task EncryptedRoomEventsGetAnExplanationOnlyWhenChatIsOnAndNeverAreDecrypted()
    {
        var on = await CreateRigAsync();
        await using var _ = on.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(on.Factory);
        on.Server.Deliver(new MatrixInboundMessage(user.Room, user.MatrixId, string.Empty, EventId: "$enc1", IsEncrypted: true));
        await PollAsync(on);
        StringAssert.Contains(on.Server.RepliesTo(user.Room).Single(), "can't read encrypted messages");

        var off = await CreateRigAsync(conversation: false);
        await using var __ = off.Factory;
        var other = await ConversationHostTests.CreateLinkedUserAsync(off.Factory);
        off.Server.Deliver(new MatrixInboundMessage(other.Room, other.MatrixId, string.Empty, EventId: "$enc2", IsEncrypted: true));
        await PollAsync(off);
        Assert.IsEmpty(off.Server.RepliesTo(other.Room), "With chat off the old behavior (ignore) is unchanged.");
    }

    [TestMethod]
    public async Task AMessageWithoutAnEventIdIsNotHandledByTheConversation()
    {
        var rig = await CreateRigAsync();
        await using var _ = rig.Factory;
        var user = await ConversationHostTests.CreateLinkedUserAsync(rig.Factory);

        rig.Server.Deliver(new MatrixInboundMessage(user.Room, user.MatrixId, "Find The Hobbit"));
        await PollAsync(rig);

        Assert.IsEmpty(rig.Server.RepliesTo(user.Room), "Without an event id a replay could not be made harmless, so nothing runs.");
    }
}
