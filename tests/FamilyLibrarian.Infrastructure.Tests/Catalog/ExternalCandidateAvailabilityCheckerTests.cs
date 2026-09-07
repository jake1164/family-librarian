using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class ExternalCandidateAvailabilityCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NoEnabledProvidersReturnsEmptyWithoutCallingTheClient()
    {
        var context = new TestContext();

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, context.Client.CallCount);
    }

    [TestMethod]
    public async Task AProviderWhoseEgressIsBlockedIsSkipped()
    {
        var context = new TestContext();
        var provider = NewProvider("blocked-source");
        provider.SetEnabled(true, null, Now);
        provider.SetEgressPolicyOverride(EgressPolicy.PrivateRequired, null, Now);
        context.Store.Providers.Add(provider);
        // Gateway cache defaults to Disabled -- PrivateEgressRouteResolver blocks PrivateRequired.

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, context.Client.CallCount);
    }

    [TestMethod]
    public async Task AProviderSearchFailureDegradesToEmptyRatherThanThrowing()
    {
        var context = new TestContext();
        var provider = NewProvider("flaky-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Throw = true;

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task AMatchingProviderReturnsADirectAcquisitionOptionWithNoWorkId()
    {
        var context = new TestContext();
        var provider = NewProvider("free-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates =
        [
            new ExternalProviderCandidate("ref-1", "Moby Dick", "Herman Melville", "epub", 500_000, null)
        ];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("free-source", option.ProviderId);
        Assert.AreEqual("ref-1", option.ProviderResultId);
        Assert.AreEqual(Guid.Empty, option.WorkId);
        Assert.AreEqual(OptionKind.DirectAcquisition, option.OptionKind);
    }

    private static ExternalProvider NewProvider(string providerId) =>
        new(providerId, providerId, "https://example.test", Now);

    private sealed class TestContext
    {
        public TestContext()
        {
            Store = new FakeExternalProviderStore();
            Client = new FakeExternalProviderClient();
            Checker = new ExternalCandidateAvailabilityChecker(
                Store,
                Client,
                new PrivateEgressRouteResolver(new FakeGatewayRuntimeCache()),
                new NoOpCredentialProtector());
        }

        public FakeExternalProviderStore Store { get; }

        public FakeExternalProviderClient Client { get; }

        public ExternalCandidateAvailabilityChecker Checker { get; }
    }

    private sealed class FakeExternalProviderStore : IExternalProviderStore
    {
        public List<ExternalProvider> Providers { get; } = [];

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

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeExternalProviderClient : IExternalProviderClient
    {
        public IReadOnlyList<ExternalProviderCandidate> Candidates { get; set; } = [];

        public bool Throw { get; set; }

        public int CallCount { get; private set; }

        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> GetHealthAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string providerReference, RequestMediaType mediaType, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Throw
                ? throw new HttpRequestException("The external provider is unavailable.")
                : Task.FromResult(Candidates);
        }
    }

    private sealed class FakeGatewayRuntimeCache : IPrivateEgressGatewayRuntimeCache
    {
        public PrivateEgressGatewayRuntimeState Current { get; private set; } = PrivateEgressGatewayRuntimeState.Disabled;

        public void Refresh(PrivateEgressGatewayRuntimeState state) => Current = state;
    }

    private sealed class NoOpCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }
}
