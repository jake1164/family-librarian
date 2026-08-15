using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ProviderAdminServiceTests
{
    private const string KeylessProviderId = "openlibrary";
    private const string CredentialedProviderId = "googlebooks";

    private static readonly IReadOnlySet<ProviderCapability> MetadataOnly =
        new HashSet<ProviderCapability> { ProviderCapability.Metadata };

    [TestMethod]
    public async Task StatusNeverExposesTheStoredCredential()
    {
        var context = new TestContext();
        await context.Service.SetCredentialAsync(
            CredentialedProviderId,
            "super-secret-key-value",
            CancellationToken.None);

        var status = await context.Service.GetStatusAsync(
            CredentialedProviderId,
            CancellationToken.None);

        Assert.IsNotNull(status);
        Assert.IsTrue(status.HasStoredCredential);

        // The hint is the last four characters only; the key itself must not be
        // reachable through any property of the status the client receives.
        Assert.AreEqual("alue", status.CredentialHint);

        var serialized = System.Text.Json.JsonSerializer.Serialize(status);
        StringAssert.DoesNotMatch(
            serialized,
            new System.Text.RegularExpressions.Regex("super-secret-key-value"));
    }

    [TestMethod]
    public async Task StoredCredentialIsEncryptedAtRest()
    {
        var context = new TestContext();

        await context.Service.SetCredentialAsync(
            CredentialedProviderId,
            "super-secret-key-value",
            CancellationToken.None);

        var stored = await context.Store.FindAsync(CredentialedProviderId, CancellationToken.None);

        Assert.IsNotNull(stored?.ProtectedCredential);
        Assert.AreNotEqual("super-secret-key-value", stored.ProtectedCredential);
        Assert.AreEqual(context.Protector.FormatVersion, stored.CredentialFormatVersion);
    }

    [TestMethod]
    public async Task EnableAndDisableRoundTripsThroughTheStore()
    {
        var context = new TestContext();

        var enabled = await context.Service.SetEnabledAsync(
            KeylessProviderId,
            true,
            CancellationToken.None);
        Assert.AreEqual(ProviderCommandOutcome.Success, enabled.Outcome);
        Assert.IsTrue(enabled.Status!.IsEnabled);

        var disabled = await context.Service.SetEnabledAsync(
            KeylessProviderId,
            false,
            CancellationToken.None);
        Assert.AreEqual(ProviderCommandOutcome.Success, disabled.Outcome);
        Assert.IsFalse(disabled.Status!.IsEnabled);

        var reread = await context.Service.GetStatusAsync(KeylessProviderId, CancellationToken.None);
        Assert.IsFalse(reread!.IsEnabled);
    }

    [TestMethod]
    public async Task ACredentialedProviderCannotBeEnabledWithoutAKey()
    {
        var context = new TestContext();

        var result = await context.Service.SetEnabledAsync(
            CredentialedProviderId,
            true,
            CancellationToken.None);

        Assert.AreEqual(ProviderCommandOutcome.Invalid, result.Outcome);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public async Task ClearingACredentialAlsoDisablesTheProvider()
    {
        var context = new TestContext();
        await context.Service.SetCredentialAsync(CredentialedProviderId, "a-key-value", CancellationToken.None);
        await context.Service.SetEnabledAsync(CredentialedProviderId, true, CancellationToken.None);

        var cleared = await context.Service.ClearCredentialAsync(
            CredentialedProviderId,
            CancellationToken.None);

        Assert.AreEqual(ProviderCommandOutcome.Success, cleared.Outcome);
        Assert.IsFalse(cleared.Status!.HasStoredCredential);
        Assert.IsFalse(cleared.Status.IsEnabled);
    }

    [TestMethod]
    public async Task AnExternallyManagedCredentialCannotBeReplacedOrCleared()
    {
        var context = new TestContext(googleBooksExternallyManaged: true);

        var set = await context.Service.SetCredentialAsync(
            CredentialedProviderId,
            "attempted-override",
            CancellationToken.None);
        var cleared = await context.Service.ClearCredentialAsync(
            CredentialedProviderId,
            CancellationToken.None);

        Assert.AreEqual(ProviderCommandOutcome.Invalid, set.Outcome);
        Assert.AreEqual(ProviderCommandOutcome.Invalid, cleared.Outcome);
    }

    [TestMethod]
    public async Task AnUnknownProviderIdIsRejected()
    {
        var context = new TestContext();

        var status = await context.Service.GetStatusAsync("not-installed", CancellationToken.None);
        var enabled = await context.Service.SetEnabledAsync("not-installed", true, CancellationToken.None);

        Assert.IsNull(status);
        Assert.AreEqual(ProviderCommandOutcome.NotFound, enabled.Outcome);
    }

    [TestMethod]
    public async Task EveryMutationWritesAnAuditEventWithoutTheSecret()
    {
        var context = new TestContext();

        await context.Service.SetCredentialAsync(CredentialedProviderId, "super-secret-key-value", CancellationToken.None);
        await context.Service.SetEnabledAsync(CredentialedProviderId, true, CancellationToken.None);
        await context.Service.ClearCredentialAsync(CredentialedProviderId, CancellationToken.None);

        Assert.IsTrue(context.Audit.Entries.Count >= 3);

        foreach (var entry in context.Audit.Entries)
        {
            var serialized = System.Text.Json.JsonSerializer.Serialize(entry.Detail);
            StringAssert.DoesNotMatch(
                serialized,
                new System.Text.RegularExpressions.Regex("super-secret-key-value"));
        }
    }

    [TestMethod]
    public async Task ReplacingACredentialInvalidatesThePreviousTestResult()
    {
        var context = new TestContext();
        await context.Service.SetCredentialAsync(CredentialedProviderId, "first-key-value", CancellationToken.None);
        await context.Service.RecordTestResultAsync(CredentialedProviderId, true, "Connected.", CancellationToken.None);

        var replaced = await context.Service.SetCredentialAsync(
            CredentialedProviderId,
            "second-key-value",
            CancellationToken.None);

        Assert.IsNull(replaced.Status!.LastTestedAtUtc);
        Assert.IsNull(replaced.Status.LastTestSucceeded);
    }

    private sealed class TestContext
    {
        public TestContext(bool googleBooksExternallyManaged = false)
        {
            Registry = new StubRegistry(googleBooksExternallyManaged);
            Store = new InMemorySettingsStore();
            Protector = new ReversibleTestProtector();
            Audit = new RecordingAuditWriter();

            Service = new ProviderAdminService(
                Registry,
                Store,
                Protector,
                Audit,
                new StubCurrentUser(),
                new FixedClock());
        }

        public StubRegistry Registry { get; }

        public InMemorySettingsStore Store { get; }

        public ReversibleTestProtector Protector { get; }

        public RecordingAuditWriter Audit { get; }

        public ProviderAdminService Service { get; }
    }

    private sealed class StubRegistry(bool googleBooksExternallyManaged) : IProviderRegistry
    {
        private readonly ProviderDescriptor[] _providers =
        [
            new("demo", "Sample catalog", MetadataOnly, RequiresCredential: false, HasExternallyManagedCredential: false, DefaultEnabled: true),
            new(KeylessProviderId, "Open Library", MetadataOnly, RequiresCredential: false, HasExternallyManagedCredential: false, DefaultEnabled: false),
            new(CredentialedProviderId, "Google Books", MetadataOnly, RequiresCredential: true, HasExternallyManagedCredential: googleBooksExternallyManaged, DefaultEnabled: false)
        ];

        public IReadOnlyList<ProviderDescriptor> GetInstalledProviders() => _providers;

        public ProviderDescriptor? Find(string providerId) =>
            _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class InMemorySettingsStore : IProviderSettingsStore
    {
        private readonly Dictionary<string, ProviderSetting> _settings =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ProviderSetting>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderSetting>>(_settings.Values.ToArray());

        public Task<ProviderSetting?> FindAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(_settings.GetValueOrDefault(providerId));

        public Task<ProviderSetting> GetOrCreateAsync(string providerId, CancellationToken cancellationToken)
        {
            if (!_settings.TryGetValue(providerId, out var setting))
            {
                setting = new ProviderSetting(providerId, DateTimeOffset.UnixEpoch);
                _settings[providerId] = setting;
            }

            return Task.FromResult(setting);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A reversible stand-in for Data Protection, so tests stay in-process. The
    /// provider id is folded into the stored value to mirror the real per-provider
    /// purpose separation.
    /// </summary>
    private sealed class ReversibleTestProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) =>
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{providerId} {plaintext}"));

        public string? Unprotect(string providerId, string protectedValue, int formatVersion)
        {
            if (formatVersion != FormatVersion)
            {
                return null;
            }

            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
            var separator = decoded.IndexOf(' ', StringComparison.Ordinal);
            if (separator < 0)
            {
                return null;
            }

            // A value written for another provider must not unprotect here.
            return string.Equals(decoded[..separator], providerId, StringComparison.OrdinalIgnoreCase)
                ? decoded[(separator + 1)..]
                : null;
        }
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<(string Action, string SubjectType, string? SubjectId, object? Detail)> Entries { get; } = [];

        public Task WriteAsync(
            string action,
            string subjectType,
            string? subjectId,
            object? detail,
            CancellationToken cancellationToken)
        {
            Entries.Add((action, subjectType, subjectId, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => Guid.Parse("11111111-1111-1111-1111-111111111111");

        public string? DisplayName => "admin@example.test";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    }
}
