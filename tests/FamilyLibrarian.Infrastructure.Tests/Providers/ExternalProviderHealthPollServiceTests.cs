using System.Security.Cryptography;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// Covers <see cref="ExternalProviderHealthPollService"/>'s independent
/// background probe -- decoupled from <see cref="ProviderRecheckSchedule"/>
/// (a Manual-schedule provider must still be checked), and every failure
/// mode recorded rather than silently skipped (egress-blocked, connection
/// failure, undecryptable credential), each participating in the same
/// notify-on-transition logic.
/// </summary>
[TestClass]
public sealed class ExternalProviderHealthPollServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly ExternalProviderHealth Healthy =
        new(ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available);
    private static readonly ExternalProviderHealth Unhealthy =
        new(ProviderHealthStatus.Unhealthy, ProviderOperationalStatus.Unavailable, ProviderOperationalStatus.Unavailable);

    [TestMethod]
    public async Task AManualScheduleEnabledProviderIsStillChecked()
    {
        var context = new TestContext();
        var provider = NewProvider("manual-source");
        provider.SetEnabled(true, null, Now);
        Assert.AreEqual(ProviderRecheckSchedule.Manual, provider.RecheckSchedule);
        context.Store.Add(provider);
        context.Client.Health = Healthy;

        var checkedCount = await context.Service.CheckAllEnabledAsync(CancellationToken.None);

        Assert.AreEqual(1, checkedCount);
        Assert.AreEqual(1, context.Client.HealthCallCount);
        Assert.IsTrue(provider.LastTestSucceeded);
        Assert.AreEqual("Available", provider.CachedSearchOperationStatus);
        Assert.AreEqual(1, context.Store.SaveChangesCallCount);
    }

    [TestMethod]
    public async Task ADisabledProviderIsNeverChecked()
    {
        var context = new TestContext();
        var provider = NewProvider("disabled-source");
        context.Store.Add(provider);

        var checkedCount = await context.Service.CheckAllEnabledAsync(CancellationToken.None);

        Assert.AreEqual(0, checkedCount);
        Assert.AreEqual(0, context.Client.HealthCallCount);
        Assert.AreEqual(0, context.Store.SaveChangesCallCount);
    }

    [TestMethod]
    public async Task AnEgressBlockedProviderIsRecordedUnhealthyRatherThanSilentlySkipped()
    {
        var context = new TestContext();
        var provider = NewProvider("vpn-source");
        provider.SetEnabled(true, null, Now);
        provider.SetEgressPolicyOverride(EgressPolicy.PrivateRequired, null, Now);
        context.Store.Add(provider);
        // Gateway cache defaults to Disabled -- PrivateEgressRouteResolver blocks PrivateRequired.

        var checkedCount = await context.Service.CheckAllEnabledAsync(CancellationToken.None);

        Assert.AreEqual(1, checkedCount);
        Assert.AreEqual(0, context.Client.HealthCallCount);
        Assert.IsFalse(provider.LastTestSucceeded);
        Assert.AreEqual("Unhealthy", provider.CachedHealthStatus);
        Assert.AreEqual("Unavailable", provider.CachedSearchOperationStatus);
        Assert.Contains("gateway", provider.LastTestMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(1, context.Repository.Events.Count);
    }

    [TestMethod]
    public async Task AConnectionFailureIsRecordedWithAnExplanatoryMessage()
    {
        var context = new TestContext();
        var provider = NewProvider("flaky-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Add(provider);
        context.Client.ThrowOnHealth = new HttpRequestException("Connection refused.");

        await context.Service.CheckAllEnabledAsync(CancellationToken.None);

        Assert.IsFalse(provider.LastTestSucceeded);
        Assert.Contains("unreachable", provider.LastTestMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection refused", provider.LastTestMessage!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnUndecryptableCredentialIsRecordedUnhealthyRatherThanLeftUntouched()
    {
        var context = new TestContext();
        var provider = NewProvider("keyed-source");
        provider.SetEnabled(true, null, Now);
        provider.SetApiKey("protected-value", 1, "abcd", null, Now);
        context.Store.Add(provider);
        context.Protector.ThrowOnUnprotect = true;

        var checkedCount = await context.Service.CheckAllEnabledAsync(CancellationToken.None);

        Assert.AreEqual(1, checkedCount);
        Assert.IsFalse(provider.LastTestSucceeded);
        Assert.AreEqual("Unhealthy", provider.CachedHealthStatus);
        Assert.Contains("stored API key", provider.LastTestMessage!, StringComparison.OrdinalIgnoreCase);
        // Recorded like any other failure -- not silently left untouched.
        Assert.AreEqual(1, context.Repository.Events.Count);
    }

    [TestMethod]
    public async Task ANeverTestedProviderGoingHealthyDoesNotNotify()
    {
        var context = new TestContext();
        var provider = NewProvider("new-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Add(provider);
        context.Client.Health = Healthy;

        await context.Service.CheckAllEnabledAsync(CancellationToken.None);

        Assert.AreEqual(0, context.Repository.Events.Count);
    }

    [TestMethod]
    public async Task ATransitionToUnhealthyFiresExactlyOneNotificationAndAStillDownPassDoesNotRecur()
    {
        var context = new TestContext();
        var provider = NewProvider("degrading-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Add(provider);
        context.Client.Health = Healthy;
        await context.Service.CheckAllEnabledAsync(CancellationToken.None);
        Assert.AreEqual(0, context.Repository.Events.Count);

        context.Client.Health = Unhealthy;
        await context.Service.CheckAllEnabledAsync(CancellationToken.None);
        Assert.AreEqual(1, context.Repository.Events.Count);
        Assert.AreEqual(1, context.Repository.Events.Single().RepeatCount);

        // Still down on the next poll tick -- no transition, so no second fire.
        await context.Service.CheckAllEnabledAsync(CancellationToken.None);
        Assert.AreEqual(1, context.Repository.Events.Count);
        Assert.AreEqual(1, context.Repository.Events.Single().RepeatCount);
    }

    [TestMethod]
    public async Task ARecoveryDoesNotNotifyButADegradeAfterRecoveryFiresAgain()
    {
        var context = new TestContext();
        var provider = NewProvider("flapping-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Add(provider);
        context.Client.Health = Unhealthy;
        await context.Service.CheckAllEnabledAsync(CancellationToken.None);
        Assert.AreEqual(1, context.Repository.Events.Count);
        Assert.AreEqual(1, context.Repository.Events.Single().RepeatCount);

        // Recovers -- chip/cache updates, but no "recovered" notification.
        context.Client.Health = Healthy;
        await context.Service.CheckAllEnabledAsync(CancellationToken.None);
        Assert.IsTrue(provider.LastTestSucceeded);
        Assert.AreEqual(1, context.Repository.Events.Count);
        Assert.AreEqual(1, context.Repository.Events.Single().RepeatCount);

        // Breaks again -- this is a real second transition, so it recurs
        // (never duplicates) the same notification row.
        context.Client.Health = Unhealthy;
        await context.Service.CheckAllEnabledAsync(CancellationToken.None);
        Assert.AreEqual(1, context.Repository.Events.Count);
        Assert.AreEqual(2, context.Repository.Events.Single().RepeatCount);
    }

    private static ExternalProvider NewProvider(string providerId) =>
        new(providerId, providerId, "https://example.test", Now);

    private sealed class TestContext
    {
        public TestContext()
        {
            Store = new FakeExternalProviderStore();
            Client = new FakeExternalProviderClient();
            Protector = new FakeCredentialProtector();
            Repository = new InMemoryNotificationRepository();
            var checker = new ExternalCandidateAvailabilityChecker(
                Store,
                Client,
                new PrivateEgressRouteResolver(new FakeGatewayRuntimeCache()),
                new ExternalProviderMatchVerifier(
                    new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()),
                    new DeterministicBookMatcher()),
                Protector);
            var notifications = new NotificationService(Repository, new StubCurrentUser(), new FixedClock());

            Service = new ExternalProviderHealthPollService(
                Store, checker, new PrivateEgressRouteResolver(new FakeGatewayRuntimeCache()), notifications, new FixedClock());
        }

        public FakeExternalProviderStore Store { get; }

        public FakeExternalProviderClient Client { get; }

        public FakeCredentialProtector Protector { get; }

        public InMemoryNotificationRepository Repository { get; }

        public ExternalProviderHealthPollService Service { get; }
    }

    private sealed class FakeExternalProviderStore : IExternalProviderStore
    {
        public List<ExternalProvider> Providers { get; } = [];

        public int SaveChangesCallCount { get; private set; }

        public Task<IReadOnlyList<ExternalProvider>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>(Providers);

        public Task<IReadOnlyList<ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>(Providers.Where(provider => provider.IsEnabled).ToArray());

        public Task<ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Providers.FirstOrDefault(provider => provider.Id == id));

        public Task<ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(Providers.FirstOrDefault(provider => provider.ProviderId == providerId));

        public void Add(ExternalProvider provider) => Providers.Add(provider);

        public void Remove(ExternalProvider provider) => Providers.Remove(provider);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExternalProviderClient : IExternalProviderClient
    {
        public ExternalProviderHealth Health { get; set; } = Healthy;

        public Exception? ThrowOnHealth { get; set; }

        public int HealthCallCount { get; private set; }

        public Task<ExternalProviderHealth> GetHealthAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken)
        {
            HealthCallCount++;
            return ThrowOnHealth is not null ? throw ThrowOnHealth : Task.FromResult(Health);
        }

        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
            string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> GetOutputAsync(
            string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAcquireAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAcquireAsync(
            string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGatewayRuntimeCache : IPrivateEgressGatewayRuntimeCache
    {
        public PrivateEgressGatewayRuntimeState Current { get; private set; } = PrivateEgressGatewayRuntimeState.Disabled;

        public void Refresh(PrivateEgressGatewayRuntimeState state) => Current = state;
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public bool ThrowOnUnprotect { get; set; }

        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) =>
            ThrowOnUnprotect ? throw new CryptographicException("The stored key could not be decrypted.") : protectedValue;
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;

        public string? DisplayName => null;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class InMemoryNotificationRepository : INotificationRepository
    {
        public List<NotificationEvent> Events { get; } = [];

        public List<NotificationReceipt> Receipts { get; } = [];

        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience,
            Guid? recipientUserId,
            string category,
            string? subjectType,
            string? subjectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Events
                .Where(notification =>
                    notification.Audience == audience &&
                    notification.RecipientUserId == recipientUserId &&
                    notification.Category == category &&
                    notification.SubjectType == subjectType &&
                    notification.SubjectId == subjectId)
                .OrderByDescending(notification => notification.LastOccurredAtUtc)
                .FirstOrDefault());

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
        {
            Events.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NotificationReceipt>>(
                Receipts.Where(receipt => receipt.NotificationEventId == notificationEventId).ToArray());

        public Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken)
        {
            foreach (var receipt in receipts)
            {
                Receipts.Remove(receipt);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
