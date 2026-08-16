using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Default-safe provider-catalog fetcher fake: no ordinary test depends on
/// reaching a real catalog URL over the network.
/// </summary>
internal sealed class AlwaysFailsProviderCatalogFetcher : IProviderCatalogFetcher
{
    public Task<ProviderCatalogFetchOutcome> FetchAsync(string url, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderCatalogFetchOutcome.Failure("No catalog is reachable in tests by default."));
}
