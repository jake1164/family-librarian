using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>Protocol v2 §8's <c>POST /acquire</c> request body.</summary>
public sealed record ExternalAcquireRequest(
    Guid RequestId,
    string CandidateReference,
    string? CandidateRevision,
    string? AcquireToken,
    RequestMediaType MediaType,
    ExternalProviderAcquisitionMode AcquisitionMode = ExternalProviderAcquisitionMode.FreeOnly);

public enum ProviderAcquireOutcome
{
    Accepted,

    /// <summary>
    /// The synchronous 409 <c>CANDIDATE_CHANGED</c> case (protocol v2 §8) —
    /// the specific acquire attempt must not be retried as-is, but the
    /// candidate should be invalidated and the request re-searched, not
    /// treated as permanently dead.
    /// </summary>
    CandidateChanged
}

public sealed record ExternalProviderAcquireSubmission(
    ProviderAcquireOutcome Outcome,
    string? JobId,
    ProviderAcquisitionJobLifecycleState? State,
    string? Phase,
    int? PollAfterSeconds)
{
    public static ExternalProviderAcquireSubmission Accepted(
        string jobId, ProviderAcquisitionJobLifecycleState state, string? phase, int? pollAfterSeconds) =>
        new(ProviderAcquireOutcome.Accepted, jobId, state, phase, pollAfterSeconds);

    public static readonly ExternalProviderAcquireSubmission CandidateChanged =
        new(ProviderAcquireOutcome.CandidateChanged, null, null, null, null);
}

/// <summary>Protocol v2 §8's <c>waiting</c>/<c>user-interaction</c> object.</summary>
public sealed record ProviderInteraction(
    string? Type, string? Message, DateTimeOffset? ExpiresAtUtc, bool? ResumeSupported, string? ActionUrl);

public sealed record ProviderProgress(double? Percent, long? BytesCompleted, long? BytesTotal, string? Message);

/// <summary>Protocol v2 §8's structured <c>error</c> object.</summary>
public sealed record ProviderJobError(
    string? Code, string? Message, bool? Retryable, int? RetryAfterSeconds, string? DetailsJson);

public sealed record ExternalProviderJobStatus(
    string JobId,
    ProviderAcquisitionJobLifecycleState State,
    string? Phase,
    ProviderInteraction? Interaction,
    ProviderProgress? Progress,
    ProviderJobError? Error,
    int? PollAfterSeconds);

/// <summary>One entry from protocol v2 §8a's <c>GET /acquire/{jobId}/outputs</c> listing.</summary>
public sealed record ExternalProviderOutput(
    string OutputId,
    ProviderOutputKind Kind,
    string? Role,
    string? Filename,
    string? ContentType,
    long? SizeBytes,
    string? Uri,
    string? UriScheme,
    string? ChecksumsJson,
    DateTimeOffset? RetentionExpiresAtUtc);
