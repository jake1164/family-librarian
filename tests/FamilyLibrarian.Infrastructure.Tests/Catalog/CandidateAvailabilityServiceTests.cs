using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// Built on a real <see cref="ServiceCollection"/> rather than plain
/// constructor injection: the service under test resolves a fresh provider
/// per parallel branch through <see cref="IServiceScopeFactory"/> (see its
/// own remarks for why), so a container is needed to exercise that path at
/// all.
/// </summary>
[TestClass]
public sealed class CandidateAvailabilityServiceTests
{
    private static readonly BookIdentity Identity = new("Moby Dick", "Herman Melville", []);

    [TestMethod]
    public async Task NoRegisteredProvidersReturnsEmptyResultsForBothMediaTypes()
    {
        using var provider = BuildServiceProvider();

        var result = await Resolve(provider).GetAvailabilityAsync(Identity, CancellationToken.None);

        Assert.AreEqual(0, result.Ebook.Count);
        Assert.AreEqual(0, result.Audiobook.Count);
    }

    [TestMethod]
    public async Task AnOwnedLibraryProviderMatchAppearsInTheCorrectMediaTypeList()
    {
        using var provider = BuildServiceProvider(
            owned: [new FakeOwnedLibraryProvider("cwa", RequestMediaType.Ebook)]);

        var result = await Resolve(provider).GetAvailabilityAsync(Identity, CancellationToken.None);

        Assert.AreEqual(1, result.Ebook.Count);
        Assert.AreEqual("cwa", result.Ebook[0].ProviderId);
        Assert.AreEqual(0, result.Audiobook.Count);
    }

    [TestMethod]
    public async Task AnAudiobookOwnedLibraryProviderMatchAppearsInTheAudiobookList()
    {
        using var provider = BuildServiceProvider(
            owned: [new FakeOwnedLibraryProvider("audiobookshelf", RequestMediaType.Audiobook)]);

        var result = await Resolve(provider).GetAvailabilityAsync(Identity, CancellationToken.None);

        Assert.AreEqual(0, result.Ebook.Count);
        Assert.AreEqual(1, result.Audiobook.Count);
        Assert.AreEqual("audiobookshelf", result.Audiobook[0].ProviderId);
    }

    [TestMethod]
    public async Task ADirectAcquisitionProviderMatchIsIncluded()
    {
        using var provider = BuildServiceProvider(
            direct: [new FakeDirectAcquisitionProvider("gutendex", RequestMediaType.Ebook)]);

        var result = await Resolve(provider).GetAvailabilityAsync(Identity, CancellationToken.None);

        Assert.AreEqual(1, result.Ebook.Count);
        Assert.AreEqual("gutendex", result.Ebook[0].ProviderId);
    }

    /// <summary>One source throwing must not prevent the others' results from appearing.</summary>
    [TestMethod]
    public async Task AFailingProviderDoesNotBlockTheOthersResults()
    {
        using var provider = BuildServiceProvider(owned:
        [
            new FakeOwnedLibraryProvider("cwa", RequestMediaType.Ebook, throwHttp: true),
            new FakeOwnedLibraryProvider("audiobookshelf", RequestMediaType.Audiobook)
        ]);

        var result = await Resolve(provider).GetAvailabilityAsync(Identity, CancellationToken.None);

        Assert.AreEqual(0, result.Ebook.Count);
        Assert.AreEqual(1, result.Audiobook.Count);
    }

    [TestMethod]
    public async Task ProvidersReceiveTheGivenIdentityUnchanged()
    {
        var target = new FakeOwnedLibraryProvider("cwa", RequestMediaType.Ebook);
        using var provider = BuildServiceProvider(owned: [target]);

        await Resolve(provider).GetAvailabilityAsync(Identity, CancellationToken.None);

        // The factory registration below always returns this same instance
        // regardless of which scope resolves it, so it directly observes the
        // call the service made from its own fresh scope.
        Assert.AreEqual(Identity, target.LastIdentity);
    }

    private static ICandidateAvailabilityService Resolve(ServiceProvider provider) =>
        provider.GetRequiredService<ICandidateAvailabilityService>();

    private static ServiceProvider BuildServiceProvider(
        IReadOnlyList<FakeOwnedLibraryProvider>? owned = null,
        IReadOnlyList<FakeDirectAcquisitionProvider>? direct = null)
    {
        var services = new ServiceCollection();

        foreach (var provider in owned ?? [])
        {
            services.AddScoped<IOwnedLibraryProvider>(_ => provider);
        }

        foreach (var provider in direct ?? [])
        {
            services.AddScoped<IDirectAcquisitionProvider>(_ => provider);
        }

        services.AddScoped(_ => (IExternalProviderStore)new NoProvidersStore());
        services.AddScoped(_ => (IExternalProviderClient)new UnusedExternalClient());
        services.AddSingleton<IPrivateEgressGatewayRuntimeCache>(new DisabledGatewayCache());
        services.AddScoped<PrivateEgressRouteResolver>();
        services.AddScoped(_ => (ICredentialProtector)new NoOpCredentialProtector());
        services.AddScoped<ExternalCandidateAvailabilityChecker>();
        services.AddScoped<ICandidateAvailabilityService, CandidateAvailabilityService>();

        return services.BuildServiceProvider();
    }

    private sealed class FakeOwnedLibraryProvider(string id, RequestMediaType matchingMediaType, bool throwHttp = false)
        : IOwnedLibraryProvider
    {
        public string Id => id;

        public BookIdentity? LastIdentity { get; private set; }

        public Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
            BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken)
        {
            LastIdentity = identity;
            if (throwHttp)
            {
                throw new HttpRequestException("The source is unavailable.");
            }

            if (mediaType != matchingMediaType)
            {
                return Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);
            }

            return Task.FromResult<IReadOnlyList<FulfillmentOption>>(
            [
                new FulfillmentOption(
                    Id, "result-1", Guid.Empty, null, mediaType, OptionKind.Owned, AcquisitionMethod.OwnedImport,
                    null, null, null, null, null, null, null, null, null, null)
            ]);
        }
    }

    private sealed class FakeDirectAcquisitionProvider(string id, RequestMediaType matchingMediaType) : IDirectAcquisitionProvider
    {
        public string Id => id;

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken)
        {
            if (mediaType != matchingMediaType)
            {
                return Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);
            }

            return Task.FromResult<IReadOnlyList<FulfillmentOption>>(
            [
                new FulfillmentOption(
                    Id, "result-1", Guid.Empty, null, mediaType, OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload,
                    null, null, null, null, 0m, null, null, null, null, null)
            ]);
        }

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoProvidersStore : IExternalProviderStore
    {
        public Task<IReadOnlyList<Domain.Providers.ExternalProvider>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Domain.Providers.ExternalProvider>>([]);

        public Task<IReadOnlyList<Domain.Providers.ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Domain.Providers.ExternalProvider>>([]);

        public Task<Domain.Providers.ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Domain.Providers.ExternalProvider?>(null);

        public Task<Domain.Providers.ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<Domain.Providers.ExternalProvider?>(null);

        public void Add(Domain.Providers.ExternalProvider provider) => throw new NotSupportedException();

        public void Remove(Domain.Providers.ExternalProvider provider) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnusedExternalClient : IExternalProviderClient
    {
        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> GetHealthAsync(string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string providerReference, RequestMediaType mediaType, EgressRoute route,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DisabledGatewayCache : IPrivateEgressGatewayRuntimeCache
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
