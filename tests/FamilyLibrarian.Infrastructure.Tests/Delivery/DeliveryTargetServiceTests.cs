using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Infrastructure.Tests.Delivery;

/// <summary>
/// Ownership, concurrency, and the create-vs-correct behavior behind the
/// ebook delivery (Kindle) settings page.
/// </summary>
[TestClass]
public sealed class DeliveryTargetServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid OtherUser = Guid.NewGuid();

    [TestMethod]
    public async Task SettingAnAddressForTheFirstTimeCreatesARow()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);

        var result = await service.SetMyKindleAddressAsync(
            "reader@kindle.com", expectedVersion: null, sendByDefault: true, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, result.Outcome);
        var stored = repository.Rows.Single();
        Assert.AreEqual(Owner, stored.UserId);
        Assert.AreEqual("reader@kindle.com", stored.Address);
        Assert.IsTrue(stored.IsEnabled);
        Assert.IsTrue(stored.SendByDefault);
    }

    [TestMethod]
    public async Task SettingTheAddressASecondTimeCorrectsTheExistingRowInstead()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);
        var first = await service.SetMyKindleAddressAsync("old@kindle.com", null, true, CancellationToken.None);

        var second = await service.SetMyKindleAddressAsync(
            "new@kindle.com", first.Target!.Version, false, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, second.Outcome);
        Assert.AreEqual(1, repository.Rows.Count);
        Assert.AreEqual("new@kindle.com", repository.Rows.Single().Address);
        Assert.IsFalse(repository.Rows.Single().SendByDefault);
    }

    [TestMethod]
    public async Task AnInvalidEmailAddressIsRejectedBeforeAnyDatabaseWork()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);

        var result = await service.SetMyKindleAddressAsync("not-an-email", null, true, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task AnAnonymousCallerRecordsNothing()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, userId: null);

        var result = await service.SetMyKindleAddressAsync("reader@kindle.com", null, true, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Unauthenticated, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task ExpectingAVersionThatDoesNotExistYetIsAConflictNotABlindCreate()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);

        var result = await service.SetMyKindleAddressAsync(
            "reader@kindle.com", expectedVersion: 1, sendByDefault: true, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Conflict, result.Outcome);
        Assert.AreEqual(0, repository.Rows.Count);
    }

    [TestMethod]
    public async Task AUserCannotSeeAnotherUsersTarget()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        repository.Seed(new DeliveryTarget(OtherUser, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "them@kindle.com", Now));
        var service = Create(repository, Owner);

        var mine = await service.GetMyKindleTargetAsync(CancellationToken.None);

        Assert.IsNull(mine);
    }

    [TestMethod]
    public async Task AUserCannotCorrectAnotherUsersTargetAndItCreatesTheirOwnInstead()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var theirs = new DeliveryTarget(OtherUser, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "them@kindle.com", Now);
        repository.Seed(theirs);
        var service = Create(repository, Owner);

        // No expectedVersion supplied means "create", but a row already exists
        // for CwaKindleEmail under a different user, so nothing should collide.
        var result = await service.SetMyKindleAddressAsync("me@kindle.com", null, true, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, result.Outcome);
        Assert.AreEqual(2, repository.Rows.Count);
        Assert.AreEqual("them@kindle.com", theirs.Address);
    }

    [TestMethod]
    public async Task EnablingRequiresAnExistingTarget()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);

        var result = await service.SetMyKindleEnabledAsync(false, expectedVersion: 0, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public async Task DisablingThenEnablingRoundTrips()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);
        var created = await service.SetMyKindleAddressAsync("reader@kindle.com", null, true, CancellationToken.None);

        var disabled = await service.SetMyKindleEnabledAsync(false, created.Target!.Version, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, disabled.Outcome);
        Assert.IsFalse(disabled.Target!.IsEnabled);
    }

    [TestMethod]
    public async Task DisablingWithAStaleVersionIsAConflict()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);
        var created = await service.SetMyKindleAddressAsync("reader@kindle.com", null, true, CancellationToken.None);

        var result = await service.SetMyKindleEnabledAsync(false, created.Target!.Version + 1, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Conflict, result.Outcome);
        Assert.IsTrue(repository.Rows.Single().IsEnabled);
    }

    [TestMethod]
    public async Task TestingKindleDeliveryWithNoProviderRegisteredFails()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner, providers: []);

        var result = await service.TestKindleDeliveryAsync(CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task TestingKindleDeliveryDelegatesToTheCwaProviderAndPassesItsOutcomeThrough()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var provider = new StubEbookDeliveryProvider("cwa", new ConnectionTestOutcome(true, "Signed in."));
        var service = Create(repository, Owner, providers: [provider]);

        var result = await service.TestKindleDeliveryAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Signed in.", result.Message);
    }

    [TestMethod]
    public async Task TestingKindleDeliveryAsAnAnonymousCallerFails()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var provider = new StubEbookDeliveryProvider("cwa", new ConnectionTestOutcome(true, "Signed in."));
        var service = Create(repository, userId: null, providers: [provider]);

        var result = await service.TestKindleDeliveryAsync(CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task AdminCanSetTheAddressForAUserWhoHasNoneYet()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);

        var result = await service.AdminSetKindleAddressAsync(
            OtherUser, "them@kindle.com", expectedVersion: null, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, result.Outcome);
        var stored = repository.Rows.Single();
        Assert.AreEqual(OtherUser, stored.UserId);
        Assert.AreEqual("them@kindle.com", stored.Address);
        Assert.IsTrue(stored.SendByDefault, "Admin address creation still defaults SendByDefault, matching self-service creation.");
    }

    [TestMethod]
    public async Task AdminCorrectingAnAddressDoesNotDisturbSendByDefault()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);
        var mine = await service.SetMyKindleAddressAsync("old@kindle.com", null, sendByDefault: false, CancellationToken.None);

        var result = await service.AdminSetKindleAddressAsync(
            Owner, "new@kindle.com", mine.Target!.Version, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, result.Outcome);
        Assert.AreEqual("new@kindle.com", repository.Rows.Single().Address);
        Assert.IsFalse(repository.Rows.Single().SendByDefault, "Admin edits must not silently flip the owner's own delivery preference.");
    }

    [TestMethod]
    public async Task AdminSettingAStaleVersionIsAConflict()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);
        var mine = await service.SetMyKindleAddressAsync("old@kindle.com", null, true, CancellationToken.None);

        var result = await service.AdminSetKindleAddressAsync(
            Owner, "new@kindle.com", mine.Target!.Version + 1, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Conflict, result.Outcome);
        Assert.AreEqual("old@kindle.com", repository.Rows.Single().Address);
    }

    [TestMethod]
    public async Task AdminCanDisableAUsersTarget()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        var service = Create(repository, Owner);
        var mine = await service.SetMyKindleAddressAsync("reader@kindle.com", null, true, CancellationToken.None);

        var result = await service.AdminSetKindleEnabledAsync(Owner, false, mine.Target!.Version, CancellationToken.None);

        Assert.AreEqual(SetKindleTargetOutcome.Success, result.Outcome);
        Assert.IsFalse(result.Target!.IsEnabled);
    }

    [TestMethod]
    public async Task AdminListingKindleTargetsReturnsEveryUsersTargetKeyedByUserId()
    {
        var repository = new InMemoryDeliveryTargetRepository();
        repository.Seed(new DeliveryTarget(Owner, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "mine@kindle.com", Now));
        repository.Seed(new DeliveryTarget(OtherUser, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "theirs@kindle.com", Now));
        var service = Create(repository, Owner);

        var all = await service.AdminListKindleTargetsAsync(CancellationToken.None);

        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("mine@kindle.com", all[Owner].Address);
        Assert.AreEqual("theirs@kindle.com", all[OtherUser].Address);
    }

    private static DeliveryTargetService Create(
        IDeliveryTargetRepository repository, Guid? userId, IEnumerable<IEbookDeliveryProvider>? providers = null) =>
        new(repository, providers ?? [], new StubCurrentUser(userId), new NoOpAuditWriter(), new FixedClock());

    private sealed class StubEbookDeliveryProvider(string id, ConnectionTestOutcome testOutcome) : IEbookDeliveryProvider
    {
        public string Id => id;

        public Task<bool> CanDeliverAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<EbookDeliveryOutcome> DeliverAsync(
            string providerBookId, string bookFormat, bool convert, string recipientEmail,
            CancellationToken cancellationToken) =>
            Task.FromResult(EbookDeliveryOutcome.Delivered("Delivered."));

        public Task<ConnectionTestOutcome> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(testOutcome);
    }

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public string? DisplayName => userId is null ? null : "Reader";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class InMemoryDeliveryTargetRepository : IDeliveryTargetRepository
    {
        public List<DeliveryTarget> Rows { get; } = [];

        public void Seed(DeliveryTarget target) => Rows.Add(target);

        public Task<DeliveryTarget?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(row => row.Id == id));

        public Task<IReadOnlyList<DeliveryTarget>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryTarget>>(
                Rows.Where(row => row.UserId == userId).ToArray());

        public Task<IReadOnlyList<DeliveryTarget>> ListAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryTarget>>(Rows.ToArray());

        public void Add(DeliveryTarget target) => Rows.Add(target);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
