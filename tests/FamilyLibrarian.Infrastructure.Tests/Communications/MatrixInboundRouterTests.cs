using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Domain.Notifications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

/// <summary>
/// COMM-1 §D: the inbound router's verification-code and yes/no promptable
/// handling. <see cref="DeliveryAttemptService"/> is real (not faked) for the
/// yes/no cases so the idempotency guarantee (§E) is exercised through the
/// same code path production uses, not re-asserted against a mock.
/// </summary>
[TestClass]
public sealed class MatrixInboundRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    [TestMethod]
    public async Task AMessageFromAnUnknownRoomIsIgnored()
    {
        var context = new TestContext();
        context.ConfigureMatrix();

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage("!unknown:example.test", "@someone:example.test", "yes"), CancellationToken.None);

        Assert.IsNull(context.MatrixClient.LastReply);
    }

    [TestMethod]
    public async Task TheCorrectVerificationCodeVerifiesTheLink()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedPendingDestination();

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, destination.VerificationCode!),
            CancellationToken.None);

        Assert.IsTrue(destination.IsVerified);
        StringAssert.Contains(context.MatrixClient.LastReply, "linked");
    }

    [TestMethod]
    public async Task AWrongVerificationCodeDoesNotVerifyTheLink()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedPendingDestination();

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "000000"), CancellationToken.None);

        Assert.IsFalse(destination.IsVerified);
        StringAssert.Contains(context.MatrixClient.LastReply, "didn't match");
    }

    [TestMethod]
    public async Task AYesReplyConfirmsTheMostRecentDeliveryConfirmationAsk()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedVerifiedDestination();
        var attempt = context.SeedSubmittedAttempt();
        context.SeedConfirmationAsk(attempt.Id);

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "yes"), CancellationToken.None);

        Assert.AreEqual(DeliveryConfirmationStatus.Confirmed, attempt.ConfirmationStatus);
        StringAssert.Contains(context.MatrixClient.LastReply, "delivered");
    }

    [TestMethod]
    public async Task ANoReplyReportsTheAttemptMissing()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedVerifiedDestination();
        var attempt = context.SeedSubmittedAttempt();
        context.SeedConfirmationAsk(attempt.Id);

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "no"), CancellationToken.None);

        Assert.AreEqual(DeliveryConfirmationStatus.ReportedMissing, attempt.ConfirmationStatus);
        StringAssert.Contains(context.MatrixClient.LastReply, "retry");
    }

    /// <summary>
    /// COMM-1 §E: replying twice -- here, twice over Matrix; the same guard
    /// covers a Matrix reply after an earlier web confirmation, since both
    /// paths funnel through <c>DeliveryAttemptService</c>'s own
    /// <see cref="DeliveryAttemptStatus.Submitted"/> guard -- must not error
    /// or double-apply. <c>DeliveryAttempt.ConfirmReceived</c> is documented
    /// as idempotent by design ("tracks the user's current answer, not a
    /// one-shot event"), so a second identical reply is expected to succeed
    /// again, not to be rejected.
    /// </summary>
    [TestMethod]
    public async Task ReplyingTwiceIsANoOpNotAnError()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedVerifiedDestination();
        var attempt = context.SeedSubmittedAttempt();
        context.SeedConfirmationAsk(attempt.Id);

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "yes"), CancellationToken.None);
        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "yes"), CancellationToken.None);

        Assert.AreEqual(DeliveryConfirmationStatus.Confirmed, attempt.ConfirmationStatus);
        StringAssert.Contains(context.MatrixClient.LastReply, "delivered");
    }

    /// <summary>A yes/no reply with no outstanding ask at all -- e.g. nothing was ever sent, or the link is new.</summary>
    [TestMethod]
    public async Task AYesReplyWithNothingOutstandingGetsAPointerInsteadOfAnError()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedVerifiedDestination();

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "yes"), CancellationToken.None);

        StringAssert.Contains(context.MatrixClient.LastReply, "nothing waiting");
    }

    [TestMethod]
    public async Task AnUnrecognizedReplyGetsAGenericPointer()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var destination = context.SeedVerifiedDestination();

        await context.Router.HandleMessageAsync(
            new MatrixInboundMessage(destination.RoomId!, destination.MatrixUserId!, "what book?"), CancellationToken.None);

        StringAssert.Contains(context.MatrixClient.LastReply, "didn't understand");
    }

    private sealed class TestContext
    {
        public FakeMatrixSettingsStore SettingsStore { get; } = new();
        public InMemoryUserMatrixDestinationStore DestinationStore { get; } = new();
        public FakeOutboundCommunicationStore CommunicationStore { get; } = new();
        public InMemoryDeliveryAttemptRepository DeliveryAttempts { get; } = new();
        public FakeMatrixClient MatrixClient { get; } = new();
        public MatrixInboundRouter Router { get; }

        public TestContext()
        {
            var clock = new FixedClock();
            var currentUser = new StubCurrentUser();
            var notifications = new NotificationService(new NullNotificationRepository(), currentUser, clock);
            var outboundCommunications = new OutboundCommunicationService(CommunicationStore, clock);
            var deliveryAttempts = new DeliveryAttemptService(
                DeliveryAttempts, new NullDeliveryTargetRepository(), [], [], currentUser, new NullAuditWriter(),
                clock, new NullCatalogRepository(), notifications, outboundCommunications);

            Router = new MatrixInboundRouter(
                DestinationStore, CommunicationStore, deliveryAttempts, SettingsStore,
                new FakeCredentialProtector(), MatrixClient, clock, new NullAuditWriter());
        }

        public void ConfigureMatrix()
        {
            var settings = SettingsStore.GetOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
            settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, Now);
            settings.SetAccessToken("protected-token", formatVersion: 1, actorUserId: null, Now);
            settings.SetEnabled(true, actorUserId: null, Now);
        }

        public UserMatrixDestination SeedPendingDestination()
        {
            var destination = new UserMatrixDestination(UserId, Now);
            destination.RequestVerification("@friend:example.test", "!room:example.test", "123456", Now);
            DestinationStore.Seed(destination);
            return destination;
        }

        public UserMatrixDestination SeedVerifiedDestination()
        {
            var destination = SeedPendingDestination();
            destination.Verify(Now);
            return destination;
        }

        public DeliveryAttempt SeedSubmittedAttempt()
        {
            var attempt = new DeliveryAttempt(
                requestId: null, UserId, Guid.NewGuid(), "cwa", "42", "epub", convert: false, attemptNumber: 1, Now, "Dune");
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
            attempt.TransitionTo(DeliveryAttemptStatus.Submitted, Now);
            DeliveryAttempts.Add(attempt);
            return attempt;
        }

        public void SeedConfirmationAsk(Guid attemptId) => CommunicationStore.All.Add(new OutboundCommunication(
            UserId, OutboundCommunicationTypes.KindleDeliveryConfirmationRequested, "Did it arrive?", null,
            "DeliveryAttempt", attemptId, null, Now));
    }

    private sealed class FakeMatrixSettingsStore : IMatrixSettingsStore
    {
        private MatrixSettings? settings;

        public Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ??= new MatrixSettings(Now));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryUserMatrixDestinationStore : IUserMatrixDestinationStore
    {
        private readonly List<UserMatrixDestination> destinations = [];

        public void Seed(UserMatrixDestination destination) => destinations.Add(destination);

        public Task<UserMatrixDestination?> FindByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(destinations.FirstOrDefault(destination => destination.UserId == userId));

        public Task<UserMatrixDestination?> FindByRoomIdAsync(string roomId, CancellationToken cancellationToken) =>
            Task.FromResult(destinations.FirstOrDefault(destination => destination.RoomId == roomId));

        public Task<UserMatrixDestination> GetOrCreateForUserAsync(
            Guid userId, DateTimeOffset createdAtUtc, CancellationToken cancellationToken)
        {
            var existing = destinations.FirstOrDefault(destination => destination.UserId == userId);
            if (existing is not null) return Task.FromResult(existing);
            var created = new UserMatrixDestination(userId, createdAtUtc);
            destinations.Add(created);
            return Task.FromResult(created);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeOutboundCommunicationStore : IOutboundCommunicationStore
    {
        public List<OutboundCommunication> All { get; } = [];

        public Task EnqueueAsync(OutboundCommunication communication, CancellationToken cancellationToken)
        {
            All.Add(communication);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboundCommunication>> GetUnprocessedBatchAsync(
            int maxCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboundCommunication>>(
                All.Where(communication => communication.ProcessedAtUtc is null).Take(maxCount).ToList());

        public Task<OutboundCommunication?> FindMostRecentByTypeAsync(
            Guid recipientUserId, string communicationType, CancellationToken cancellationToken) =>
            Task.FromResult(All
                .Where(communication => communication.RecipientUserId == recipientUserId && communication.CommunicationType == communicationType)
                .MaxBy(communication => communication.CreatedAtUtc));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryDeliveryAttemptRepository : IDeliveryAttemptRepository
    {
        private readonly List<DeliveryAttempt> rows = [];

        public void Add(DeliveryAttempt attempt) => rows.Add(attempt);

        public Task<DeliveryAttempt?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(rows.FirstOrDefault(attempt => attempt.Id == id));

        public Task<IReadOnlyList<DeliveryAttempt>> ListForRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeliveryAttempt>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeliveryAttemptView>> ListRecentAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeliveryAttempt>> ListRetryableFailedAsync(
            DateTimeOffset olderThanUtc, int maxAttemptNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeliveryAttempt?> FindLatestAsync(Guid deliveryId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeliveryTarget?> GetEligibleTargetAsync(DeliveryAttempt attempt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAddAsync(DeliveryAttempt attempt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryTransitionAsync(DeliveryAttempt attempt, DeliveryAttemptStatus status, DateTimeOffset atUtc,
            CancellationToken cancellationToken, string? reason = null, bool retryable = false) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeliveryAttempt>> ListUnfinishedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReadyRequestDelivery>> ListUnreleasedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullDeliveryTargetRepository : IDeliveryTargetRepository
    {
        public Task<DeliveryTarget?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<DeliveryTarget?>(null);

        public Task<IReadOnlyList<DeliveryTarget>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryTarget>>([]);

        public Task<IReadOnlyList<DeliveryTarget>> ListAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryTarget>>([]);

        public void Add(DeliveryTarget target)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullCatalogRepository : ICatalogRepository
    {
        public Task<FamilyLibrarian.Domain.Catalog.Work?> FindWorkByExternalReferenceAsync(
            string providerId, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Work?>(null);

        public Task<FamilyLibrarian.Domain.Catalog.Work?> FindWorkByIsbn13Async(
            IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Work?>(null);

        public Task<FamilyLibrarian.Domain.Catalog.Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Work?>(null);

        public Task<IReadOnlyList<FamilyLibrarian.Domain.Catalog.ExternalReference>> GetWorkSourcesAsync(
            Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FamilyLibrarian.Domain.Catalog.ExternalReference>>([]);

        public Task<FamilyLibrarian.Domain.Catalog.Author?> FindAuthorByNormalizedNameAsync(
            string normalizedName, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Author?>(null);

        public Task<FamilyLibrarian.Domain.Catalog.Series?> FindSeriesByNormalizedNameAsync(
            string normalizedName, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Series?>(null);

        public Task<FamilyLibrarian.Domain.Catalog.Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Series?>(null);

        public Task<FamilyLibrarian.Domain.Catalog.Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
            Task.FromResult<FamilyLibrarian.Domain.Catalog.Author?>(null);

        public void AddWork(FamilyLibrarian.Domain.Catalog.Work work)
        {
        }

        public void AddAuthor(FamilyLibrarian.Domain.Catalog.Author author)
        {
        }

        public void AddSeries(FamilyLibrarian.Domain.Catalog.Series series)
        {
        }

        public void AddExternalReference(FamilyLibrarian.Domain.Catalog.ExternalReference externalReference)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullNotificationRepository : INotificationRepository
    {
        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience, Guid? recipientUserId, string category, string? subjectType, string? subjectId,
            CancellationToken cancellationToken) => Task.FromResult<NotificationEvent?>(null);

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NotificationReceipt>>([]);

        public Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(NotificationEvent, NotificationReceipt?)>>([]);

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<NotificationReceipt?>(null);

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? DisplayName => null;
    }

    private sealed class FakeMatrixClient : IMatrixClient
    {
        public string? LastReply { get; private set; }

        public Task<ConnectionTestOutcome> TestConnectionAsync(
            MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestOutcome(true, "ok"));

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
            MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixRoomResult.Success("!room:example.test"));

        public Task<SendResult> SendMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
        {
            LastReply = text;
            return Task.FromResult(SendResult.Success());
        }

        public Task<MatrixSyncResult> SyncAsync(
            MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixSyncResult.Success(since, []));

        public Task<MatrixSendResult> SendRichMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SendResult> EditMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody,
            string htmlBody, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;
        public string Protect(string providerId, string plaintext) => plaintext;
        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }
}
