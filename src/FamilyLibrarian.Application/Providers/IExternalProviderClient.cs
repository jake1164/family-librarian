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

    Task<ExternalProviderHealth> GetHealthAsync(
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
    /// <remarks>
    /// This is the protocol-version-1 call shape, kept as a convenience for
    /// existing callers. It does not use durable jobs, idempotency, or the
    /// generalized outputs model from protocol version 2
    /// (<c>docs/04-external-provider-http-protocol.md</c>) — those land with
    /// the durable-job/background-poller work. A v2-negotiated provider is
    /// still reachable through this method; the implementation just doesn't
    /// yet exercise v2's richer job lifecycle.
    /// </remarks>
    Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, EgressRoute route,
        CancellationToken cancellationToken);

    /// <summary>
    /// Protocol v2 §8: submits <c>POST /acquire</c> and returns immediately
    /// with the job's initial representation — no polling loop inside this
    /// call. <paramref name="idempotencyKey"/> is sent as the
    /// <c>Idempotency-Key</c> header; a retried submission with the same key
    /// must resolve to the same logical job, never a duplicate.
    /// </summary>
    Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, EgressRoute route,
        CancellationToken cancellationToken);

    /// <summary>One <c>GET /acquire/{jobId}</c> poll tick — the caller decides cadence from <see cref="ExternalProviderJobStatus.PollAfterSeconds"/>.</summary>
    Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken);

    /// <summary><c>GET /acquire/{jobId}/outputs</c> — call once <c>state = completed</c>.</summary>
    Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken);

    /// <summary><c>GET /acquire/{jobId}/outputs/{outputId}</c> — not valid for a <c>uri</c>-kind output, which has no bytes to fetch.</summary>
    Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, EgressRoute route,
        CancellationToken cancellationToken);

    /// <summary><c>POST /acquire/{jobId}/cancel</c> — try to stop active work; best-effort.</summary>
    Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken);

    /// <summary>
    /// Optional <c>waiting-interaction-control</c> operation. The caller must
    /// first verify that the provider advertised that feature; defaulting to a
    /// failure keeps existing v1/v2 provider fakes and implementations
    /// backward compatible.
    /// </summary>
    Task<ExternalProviderJobStatus> StartInteractionAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The provider does not support interaction control.");

    /// <summary>Optional <c>waiting-interaction-control</c> fallback operation.</summary>
    Task<ExternalProviderJobStatus> UseAcquireFallbackAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The provider does not support interaction control.");

    /// <summary><c>DELETE /acquire/{jobId}</c> — release retained resources; best-effort.</summary>
    Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, EgressRoute route, CancellationToken cancellationToken);
}

/// <summary>
/// <c>mediaTypes</c>/<c>operations</c>/<c>features</c> per protocol v2
/// §4 — all open string lists; unrecognized values are simply carried
/// through, never rejected.
/// </summary>
public sealed record ProviderCapabilities(
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Features)
{
    public static readonly ProviderCapabilities Empty = new([], [], []);
}

public sealed record ExternalProviderManifest(
    IReadOnlyList<string> ProtocolVersions,
    string ProtocolVersion,
    string? InstanceId,
    string Id,
    string Name,
    string Version,
    ProviderCapabilities Capabilities,
    int? OutputRetentionSeconds,
    string? ManagementUrl,
    string? DocumentationUrl,
    string EgressPolicy);

public enum ProviderHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

public enum ProviderOperationalStatus
{
    Available,
    Degraded,
    Unavailable
}

/// <summary>Protocol v2 §5's structured health body, with the search/acquire operations split.</summary>
public sealed record ExternalProviderHealth(
    ProviderHealthStatus Status, ProviderOperationalStatus Search, ProviderOperationalStatus Acquire)
{
    public static readonly ExternalProviderHealth Unreachable =
        new(ProviderHealthStatus.Unhealthy, ProviderOperationalStatus.Unavailable, ProviderOperationalStatus.Unavailable);

    /// <summary>True when the provider is not reporting itself fully unhealthy — the old v1 binary signal.</summary>
    public bool IsHealthy => Status != ProviderHealthStatus.Unhealthy;

    /// <summary>
    /// True when this provider can currently do the two things Family
    /// Librarian actually depends on it for. Protocol v2 §5: "Family
    /// Librarian treats operations as the more specific signal when [status
    /// and operations] disagree" — so this looks at <see cref="Search"/>/
    /// <see cref="Acquire"/>, not the coarse <see cref="Status"/> alone. A
    /// provider can legitimately report <c>status: degraded</c> while both
    /// operations are still <c>available</c> (e.g. elevated latency) — that
    /// is still fully operational. Prefer this over <see cref="IsHealthy"/>
    /// for any decision that gates an actual search or acquire attempt;
    /// <see cref="IsHealthy"/> alone is too loose for that (a provider can be
    /// merely "not unhealthy" while unable to search or acquire anything).
    /// </summary>
    public bool IsFullyOperational =>
        Search != ProviderOperationalStatus.Unavailable && Acquire != ProviderOperationalStatus.Unavailable;
}

/// <summary>Protocol v2 §6's <c>POST /search</c> request body.</summary>
public sealed record ExternalProviderSearchRequest(
    Guid RequestId,
    RequestMediaType MediaType,
    ExternalProviderWorkEvidence Work,
    ExternalProviderEditionEvidence? Edition = null,
    ExternalProviderSearchConstraints? Constraints = null,
    ExternalProviderSearchPagination? Pagination = null);

/// <summary>
/// Protocol v2 §6/§7 candidate evidence — a provider supplies this, Family
/// Librarian decides identity from it (never the reverse). <see cref="Title"/>/
/// <see cref="Author"/>/<see cref="Format"/>/<see cref="SizeBytes"/> are
/// convenience projections of <see cref="Work"/>/<see cref="Release"/> for
/// callers that only need the simple case (e.g. <c>ExternalProviderMatchVerifier</c>'s
/// title/author corroboration) — they do not carry independent information.
/// </summary>
public sealed record ExternalProviderCandidate(
    string ProviderReference,
    ExternalProviderWorkEvidence Work,
    ExternalProviderEditionEvidence? Edition = null,
    ExternalProviderReleaseEvidence? Release = null,
    string? CandidateRevision = null,
    string? AcquireToken = null,
    string? ExtensionsJson = null,
    Uri? InspectionUri = null)
{
    public string Title => Work.Title;

    public string? Author => Work.Authors.Count > 0 ? Work.Authors[0].Name : null;

    public string? Format => Release?.Format;

    public long? SizeBytes => Release?.SizeBytes;

    /// <summary>
    /// Convenience for a provider (or test double) with nothing richer than
    /// the old flat title/author/format/size shape to offer — a simple
    /// provider is never forced to populate work/edition/release evidence
    /// it doesn't have (protocol v2 Rule 7).
    /// </summary>
    public static ExternalProviderCandidate FromSimple(
        string providerReference, string title, string? author, string? format, long? sizeBytes) =>
        new(
            providerReference,
            new ExternalProviderWorkEvidence(
                title, null, author is null ? [] : [new BookAuthor(author, "author")], [], []),
            Release: format is null && sizeBytes is null
                ? null
                : new ExternalProviderReleaseEvidence(null, format, sizeBytes, null, null, null, null, null, [], null));
}

public sealed record ExternalProviderArtifact(Stream Content, string Filename);

/// <summary>Where an outbound call to an external provider is routed.</summary>
public abstract record EgressRoute
{
    public static readonly EgressRoute Direct = new DirectRoute();

    public static EgressRoute ViaGateway(Uri proxyEndpoint) => new GatewayRoute(proxyEndpoint);

    private sealed record DirectRoute : EgressRoute;

    public sealed record GatewayRoute(Uri ProxyEndpoint) : EgressRoute;
}
