using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Default-safe external-provider client fake: no ordinary test depends on
/// reaching a real external-provider process, even the in-repo sample one.
/// </summary>
internal sealed class AlwaysEmptyExternalProviderClient : IExternalProviderClient
{
    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task<bool> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderCandidate>>([]);

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");
}
