using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Speaks the versioned external-provider HTTP protocol. Deliberately
/// low-level: every call takes an explicit <see cref="EgressRoute"/> decided
/// by the caller (<see cref="PrivateEgressRouteResolver"/>), so this type has
/// no opinion of its own about when a provider's traffic must be routed
/// through the private-egress gateway.
/// </summary>
public interface IExternalProviderClient
{
    Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken);

    Task<bool> GetHealthAsync(
        string baseUrl, string? apiKey, EgressRoute route, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, EgressRoute route,
        CancellationToken cancellationToken);

    /// <summary>
    /// Submits an acquire job, polls it to completion within a bounded
    /// timeout, and downloads the resulting artifact — or throws
    /// <see cref="HttpRequestException"/>/<see cref="TimeoutException"/> on
    /// failure. Cancelling <paramref name="cancellationToken"/> best-effort
    /// cancels the remote job too.
    /// </summary>
    Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken);
}

public sealed record ExternalProviderManifest(
    string ProtocolVersion,
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> Capabilities,
    string EgressPolicy);

public sealed record ExternalProviderSearchRequest(
    Guid RequestId,
    RequestMediaType MediaType,
    string Title,
    IReadOnlyList<string> Authors,
    string? Isbn13);

public sealed record ExternalProviderCandidate(
    string ProviderReference,
    string Title,
    string? Author,
    string? Format,
    long? SizeBytes,
    string? MetadataJson);

public sealed record ExternalProviderArtifact(Stream Content, string Filename);

/// <summary>Where an outbound call to an external provider is routed.</summary>
public abstract record EgressRoute
{
    public static readonly EgressRoute Direct = new DirectRoute();

    public static EgressRoute ViaGateway(Uri proxyEndpoint) => new GatewayRoute(proxyEndpoint);

    private sealed record DirectRoute : EgressRoute;

    public sealed record GatewayRoute(Uri ProxyEndpoint) : EgressRoute;
}
