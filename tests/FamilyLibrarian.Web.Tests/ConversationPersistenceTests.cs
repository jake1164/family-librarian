using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Infrastructure.Conversation;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M2-3 against a real database: session state, processed-event claims, request keys, atomic rollback and pacing.</summary>
[TestClass]
public sealed class ConversationPersistenceTests
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

    private static async Task<Guid> AnyUserAsync(AppDbContext database) => await database.Users.Select(user => user.Id).FirstAsync();

    private static string Unique() => Guid.NewGuid().ToString("N");

    [TestMethod]
    public async Task ASessionRoundTripsItsBoundedStateAndIsReplacedNotDuplicated()
    {
        await using var scope = WebTestFixture.Require(fixture).Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new EfConversationSessionStore(database);
        var userId = await AnyUserAsync(database);
        var room = $"!{Unique()}:example.test";
        var now = DateTimeOffset.UtcNow;
        var state = new ConversationState { Phase = ConversationPhase.ChoosingBook, Query = "The Lantern Road", Choices = [new("demo", "x1", "The Lantern Road", "Mara Quill", "2021")] };

        await store.SaveAsync(new ConversationSession(userId, "matrix", room, state, 1, now, now), CancellationToken.None);
        var loaded = await store.FindAsync(userId, "matrix", room, CancellationToken.None);
        loaded!.State.Phase = ConversationPhase.Idle;
        loaded.TurnCount = 2;
        await store.SaveAsync(loaded, CancellationToken.None);
        database.ChangeTracker.Clear();

        var again = await store.FindAsync(userId, "matrix", room, CancellationToken.None);
        Assert.AreEqual(2, again!.TurnCount);
        Assert.AreEqual(ConversationPhase.Idle, again.State.Phase);
        Assert.AreEqual(1, await database.ConversationSessions.CountAsync(session => session.ScopeKey == room));
        Assert.IsNull(await store.FindAsync(userId, "matrix", $"!other-{Unique()}:example.test", CancellationToken.None));
        Assert.IsNull(await store.FindAsync(userId, "web", room, CancellationToken.None), "Sessions are per transport.");
    }

    [TestMethod]
    public async Task AnEventIsClaimedExactlyOnceAndKeepsItsReplyAndDeliveryState()
    {
        await using var scope = WebTestFixture.Require(fixture).Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<IConversationEventLog>();
        var userId = await AnyUserAsync(database);
        var room = $"!{Unique()}:example.test";

        Assert.IsTrue(await log.TryClaimAsync(userId, "matrix", room, "$e1", CancellationToken.None));
        Assert.IsFalse(await log.TryClaimAsync(userId, "matrix", room, "$e1", CancellationToken.None), "A second claim of the same event is refused.");
        Assert.IsTrue(await log.TryClaimAsync(userId, "matrix", room, "$e2", CancellationToken.None), "Other events are independent.");
        await log.CompleteAsync("matrix", room, "$e1", "{\"reply\":true}", CancellationToken.None);
        var pending = await log.FindAsync("matrix", room, "$e1", CancellationToken.None);
        await log.MarkDeliveredAsync("matrix", room, "$e1", CancellationToken.None);
        var delivered = await log.FindAsync("matrix", room, "$e1", CancellationToken.None);

        Assert.AreEqual("{\"reply\":true}", pending!.ReplyJson);
        Assert.IsNull(pending.DeliveredAtUtc);
        Assert.IsNotNull(delivered!.DeliveredAtUtc);
        Assert.AreEqual(userId, delivered.UserId);
    }

    [TestMethod]
    public async Task ConcurrentClaimsOfTheSameEventHaveExactlyOneWinner()
    {
        var host = WebTestFixture.Require(fixture);
        Guid userId;
        await using (var setup = host.Services.CreateAsyncScope())
        {
            userId = await AnyUserAsync(setup.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        var room = $"!{Unique()}:example.test";
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IConversationEventLog>()
                .TryClaimAsync(userId, "matrix", room, "$race", CancellationToken.None);
        })).ToArray();
        start.Set();

        var results = await Task.WhenAll(attempts);

        Assert.AreEqual(1, results.Count(won => won));
    }

    [TestMethod]
    public async Task ARequestKeyIsClaimedOnceAndRemembersTheRequestItProduced()
    {
        await using var scope = WebTestFixture.Require(fixture).Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<IConversationEventLog>();
        var userId = await AnyUserAsync(database);
        var key = ConversationService.RequestKey("matrix", $"!{Unique()}:example.test", "$k");
        var requestId = Guid.NewGuid();

        var first = await log.TryClaimRequestKeyAsync(userId, key, CancellationToken.None);
        await log.AttachRequestAsync(key, requestId, CancellationToken.None);
        var second = await log.TryClaimRequestKeyAsync(userId, key, CancellationToken.None);

        Assert.IsTrue(first.Claimed);
        Assert.IsFalse(second.Claimed);
        Assert.AreEqual(requestId, second.ExistingRequestId);
    }

    [TestMethod]
    public async Task AFailureInsideTheTransactionRollsBackEveryWriteMadeInIt()
    {
        await using var scope = WebTestFixture.Require(fixture).Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<IConversationEventLog>();
        var sessions = scope.ServiceProvider.GetRequiredService<IConversationSessionStore>();
        var transaction = scope.ServiceProvider.GetRequiredService<IConversationTransaction>();
        var userId = await AnyUserAsync(database);
        var room = $"!{Unique()}:example.test";
        var key = ConversationService.RequestKey("matrix", room, "$crash");
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transaction.ExecuteAsync<bool>(async token =>
        {
            Assert.IsTrue(await log.TryClaimAsync(userId, "matrix", room, "$crash", token));
            Assert.IsTrue((await log.TryClaimRequestKeyAsync(userId, key, token)).Claimed);
            await sessions.SaveAsync(new ConversationSession(userId, "matrix", room, ConversationState.Empty(), 1, now, now), token);
            throw new InvalidOperationException("crash before commit");
        }, CancellationToken.None));

        Assert.IsNull(await log.FindAsync("matrix", room, "$crash", CancellationToken.None), "The claim did not survive.");
        Assert.IsTrue((await log.TryClaimRequestKeyAsync(userId, key, CancellationToken.None)).Claimed, "Neither did the request key.");
        Assert.IsNull(await sessions.FindAsync(userId, "matrix", room, CancellationToken.None));
        Assert.IsTrue(await log.TryClaimAsync(userId, "matrix", room, "$crash", CancellationToken.None), "The replay can therefore run the whole turn.");
    }

    [TestMethod]
    public async Task ACommittedTransactionKeepsAllItsWrites()
    {
        await using var scope = WebTestFixture.Require(fixture).Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<IConversationEventLog>();
        var sessions = scope.ServiceProvider.GetRequiredService<IConversationSessionStore>();
        var userId = await AnyUserAsync(database);
        var room = $"!{Unique()}:example.test";
        var now = DateTimeOffset.UtcNow;

        await scope.ServiceProvider.GetRequiredService<IConversationTransaction>().ExecuteAsync(async token =>
        {
            await log.TryClaimAsync(userId, "matrix", room, "$ok", token);
            await sessions.SaveAsync(new ConversationSession(userId, "matrix", room, ConversationState.Empty(), 1, now, now), token);
            return true;
        }, CancellationToken.None);

        Assert.IsNotNull(await log.FindAsync("matrix", room, "$ok", CancellationToken.None));
        Assert.IsNotNull(await sessions.FindAsync(userId, "matrix", room, CancellationToken.None));
    }

    [TestMethod]
    public async Task ProcessedEventsOlderThanTheRetentionAreRemovedButRequestKeysAreKept()
    {
        await using var scope = WebTestFixture.Require(fixture).Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await AnyUserAsync(database);
        var room = $"!{Unique()}:example.test";
        var old = DateTimeOffset.UtcNow - EfConversationEventLog.EventRetention - TimeSpan.FromDays(1);
        var oldLog = new EfConversationEventLog(database, new FixedClock(old));
        var key = ConversationService.RequestKey("matrix", room, "$old");
        await oldLog.TryClaimAsync(userId, "matrix", room, "$old", CancellationToken.None);
        await oldLog.TryClaimRequestKeyAsync(userId, key, CancellationToken.None);

        await scope.ServiceProvider.GetRequiredService<IConversationEventLog>().TryClaimAsync(userId, "matrix", room, "$new", CancellationToken.None);

        Assert.IsNull(await oldLog.FindAsync("matrix", room, "$old", CancellationToken.None));
        Assert.IsFalse((await oldLog.TryClaimRequestKeyAsync(userId, key, CancellationToken.None)).Claimed, "The key outlives its event.");
    }

    [TestMethod]
    public async Task TheLimiterAllowsOneTurnAtATimePerUserAndCapsTheRate()
    {
        var clock = new ManualTimeProvider();
        var limiter = new ConversationLimiter(new ConversationOptions { MaxMessagesPerMinute = 3 }, clock);
        var user = Guid.NewGuid();

        using var first = await limiter.TryEnterAsync(user, CancellationToken.None);
        Assert.IsNotNull(first);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(() => limiter.TryEnterAsync(user, cancellation.Token));
        first.Dispose();

        using (var second = await limiter.TryEnterAsync(user, CancellationToken.None)) Assert.IsNotNull(second);
        Assert.IsNull(await limiter.TryEnterAsync(user, CancellationToken.None), "The fourth message inside a minute is refused (the cancelled attempt counted too).");
        clock.Advance(TimeSpan.FromSeconds(61));
        using var later = await limiter.TryEnterAsync(user, CancellationToken.None);
        Assert.IsNotNull(later, "The window rolls.");

        using var other = await limiter.TryEnterAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.IsNotNull(other, "Another user is not affected.");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
