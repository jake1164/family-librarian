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

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(ExternalProviderHealth.Unreachable);

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderCandidate>>([]);

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No external provider is reachable in tests by default.");
}
