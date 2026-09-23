namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// Durable tracking for one in-flight external-provider acquisition —
/// protocol v2's <c>state</c>/<c>phase</c>/outputs job model
/// (<c>docs/04-external-provider-http-protocol.md</c> §8). Created the
/// moment Family Librarian submits <c>POST /acquire</c>, long before any
/// file exists; <see cref="AcquisitionJob"/> remains the separate, post-hoc
/// audit record created only once bytes are actually staged — this type is
/// upstream plumbing for durably tracking the remote job itself, including
/// across a Family Librarian restart, not a replacement for that audit trail.
/// </summary>
public sealed class ProviderAcquisitionJob
{
    private readonly List<ProviderAcquisitionJobOutput> _outputs = [];

    private ProviderAcquisitionJob()
    {
    }

    public ProviderAcquisitionJob(
        Guid requestId,
        Guid requestFormatId,
        Guid externalProviderId,
        string providerId,
        string? providerInstanceId,
        string idempotencyKey,
        string candidateReference,
        string? candidateRevision,
        string? acquireToken,
        DateTimeOffset createdAtUtc)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A request ID is required.", nameof(requestId));
        }

        if (requestFormatId == Guid.Empty)
        {
            throw new ArgumentException("A request format ID is required.", nameof(requestFormatId));
        }

        if (externalProviderId == Guid.Empty)
        {
            throw new ArgumentException("An external provider ID is required.", nameof(externalProviderId));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(candidateReference))
        {
            throw new ArgumentException("A candidate reference is required.", nameof(candidateReference));
        }

        Id = Guid.NewGuid();
        RequestId = requestId;
        RequestFormatId = requestFormatId;
        ExternalProviderId = externalProviderId;
        ProviderId = providerId.Trim();
        ProviderInstanceId = string.IsNullOrWhiteSpace(providerInstanceId) ? null : providerInstanceId.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        CandidateReference = candidateReference.Trim();
        CandidateRevision = string.IsNullOrWhiteSpace(candidateRevision) ? null : candidateRevision.Trim();
        AcquireToken = acquireToken;
        LifecycleState = ProviderAcquisitionJobLifecycleTransitions.InitialState;
        // Poll immediately — the caller submits and the poller picks it up
        // on its very next pass rather than waiting a full interval.
        NextPollAtUtc = createdAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public Guid RequestFormatId { get; private set; }

    public Guid ExternalProviderId { get; private set; }

    /// <summary>Snapshotted at submit time so a later provider-container swap (a changed <c>instanceId</c>) is detectable.</summary>
    public string ProviderId { get; private set; } = null!;

    public string? ProviderInstanceId { get; private set; }

    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>The provider's own job id, set once <c>POST /acquire</c> is accepted.</summary>
    public string? ProviderJobId { get; private set; }

    public string CandidateReference { get; private set; } = null!;

    public string? CandidateRevision { get; private set; }

    public string? AcquireToken { get; private set; }

    public ProviderAcquisitionJobLifecycleState LifecycleState { get; private set; }

    /// <summary>Open string — never validated against a closed vocabulary (protocol v2 §8).</summary>
    public string? Phase { get; private set; }

    public string? InteractionType { get; private set; }

    public string? InteractionMessage { get; private set; }

    public DateTimeOffset? InteractionExpiresAtUtc { get; private set; }

    public bool? InteractionResumeSupported { get; private set; }

    /// <summary>
    /// Legacy protocol-v2 interaction URL storage. Family Librarian no longer
    /// persists this value: a provider's control URL must never flow into a
    /// requester projection, and the forthcoming broker will use a dedicated
    /// server-only interaction record instead.
    /// </summary>
    public string? InteractionActionUrl { get; private set; }

    public double? ProgressPercent { get; private set; }

    public long? ProgressBytesCompleted { get; private set; }

    public long? ProgressBytesTotal { get; private set; }

    public string? ProgressMessage { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool? ErrorRetryable { get; private set; }

    public int? ErrorRetryAfterSeconds { get; private set; }

    /// <summary>Opaque provider-reported error detail, never parsed as trusted fact.</summary>
    public string? ErrorDetailsJson { get; private set; }

    public DateTimeOffset? RetentionExpiresAtUtc { get; private set; }

    /// <summary>
    /// When the background poller should next check this job — driven by the
    /// provider's own <c>Retry-After</c>/<c>pollAfterSeconds</c> hint. Terminal
    /// jobs (<see cref="ProviderAcquisitionJobLifecycleState.Completed"/>/
    /// <see cref="ProviderAcquisitionJobLifecycleState.Failed"/>/
    /// <see cref="ProviderAcquisitionJobLifecycleState.Cancelled"/>) clear this
    /// to <c>null</c> so they drop out of the poller's due-work query.
    /// </summary>
    public DateTimeOffset? NextPollAtUtc { get; private set; }

    /// <summary>Namespaced, provider-specific data (protocol v2 §11) — inert, never used for matching/trust decisions.</summary>
    public string? ExtensionsJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<ProviderAcquisitionJobOutput> Outputs => _outputs;

    /// <summary>Records the provider's initial acceptance of <c>POST /acquire</c>.</summary>
    public void RecordSubmission(
        string providerJobId, ProviderAcquisitionJobLifecycleState initialState, DateTimeOffset? nextPollAtUtc,
        DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(providerJobId))
        {
            throw new ArgumentException("A provider job id is required.", nameof(providerJobId));
        }

        ProviderJobId = providerJobId.Trim();
        ApplyState(initialState, phase: null, atUtc);
        NextPollAtUtc = nextPollAtUtc ?? atUtc;
        UpdatedAtUtc = atUtc;
    }

    /// <exception cref="InvalidProviderAcquisitionJobTransitionException">
    /// The move is not in <see cref="ProviderAcquisitionJobLifecycleTransitions"/>.
    /// </exception>
    public void ApplyStatus(
        ProviderAcquisitionJobLifecycleState state,
        string? phase,
        string? interactionType,
        string? interactionMessage,
        DateTimeOffset? interactionExpiresAtUtc,
        bool? interactionResumeSupported,
        string? interactionActionUrl,
        double? progressPercent,
        long? progressBytesCompleted,
        long? progressBytesTotal,
        string? progressMessage,
        DateTimeOffset? nextPollAtUtc,
        DateTimeOffset atUtc)
    {
        ApplyState(state, phase, atUtc);

        InteractionType = interactionType;
        InteractionMessage = interactionMessage;
        InteractionExpiresAtUtc = interactionExpiresAtUtc;
        InteractionResumeSupported = interactionResumeSupported;
        // The v2 descriptor is accepted for compatibility, but is not kept.
        // It is neither a safe requester-facing link nor sufficient to grant a
        // browser access to a provider-side human-verification session.
        InteractionActionUrl = null;

        ProgressPercent = progressPercent;
        ProgressBytesCompleted = progressBytesCompleted;
        ProgressBytesTotal = progressBytesTotal;
        ProgressMessage = progressMessage;

        NextPollAtUtc = IsTerminal(state) ? null : nextPollAtUtc;
        UpdatedAtUtc = atUtc;
    }

    /// <summary>
    /// Records a structured job failure (protocol v2 §8's <c>error</c>
    /// object) including the <c>CANDIDATE_CHANGED</c> case — the caller
    /// decides whether that specific code should trigger a re-search rather
    /// than treating the whole request as dead; this only records the
    /// terminal state.
    /// </summary>
    public void RecordFailure(
        string? errorCode, string? errorMessage, bool? retryable, int? retryAfterSeconds, string? detailsJson,
        DateTimeOffset atUtc)
    {
        ApplyState(ProviderAcquisitionJobLifecycleState.Failed, phase: null, atUtc);
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ErrorRetryable = retryable;
        ErrorRetryAfterSeconds = retryAfterSeconds;
        ErrorDetailsJson = detailsJson;
        NextPollAtUtc = null;
        UpdatedAtUtc = atUtc;
    }

    public void SetRetention(DateTimeOffset? expiresAtUtc, DateTimeOffset atUtc)
    {
        RetentionExpiresAtUtc = expiresAtUtc;
        UpdatedAtUtc = atUtc;
    }

    public void SetExtensions(string? extensionsJson, DateTimeOffset atUtc)
    {
        ExtensionsJson = extensionsJson;
        UpdatedAtUtc = atUtc;
    }

    public ProviderAcquisitionJobOutput AddOutput(
        string outputId,
        ProviderOutputKind kind,
        string? role,
        string? filename,
        string? contentType,
        long? sizeBytes,
        string? uri,
        string? uriScheme,
        string? checksumsJson,
        DateTimeOffset? retentionExpiresAtUtc,
        DateTimeOffset atUtc)
    {
        var output = new ProviderAcquisitionJobOutput(
            Id, outputId, kind, role, filename, contentType, sizeBytes, uri, uriScheme, checksumsJson,
            retentionExpiresAtUtc, atUtc);
        _outputs.Add(output);
        UpdatedAtUtc = atUtc;
        return output;
    }

    private void ApplyState(ProviderAcquisitionJobLifecycleState to, string? phase, DateTimeOffset atUtc)
    {
        if (!ProviderAcquisitionJobLifecycleTransitions.IsAllowed(LifecycleState, to))
        {
            throw new InvalidProviderAcquisitionJobTransitionException(LifecycleState, to);
        }

        LifecycleState = to;
        Phase = phase;
        UpdatedAtUtc = atUtc;
    }

    private static bool IsTerminal(ProviderAcquisitionJobLifecycleState state) => state is
        ProviderAcquisitionJobLifecycleState.Completed or
        ProviderAcquisitionJobLifecycleState.Failed or
        ProviderAcquisitionJobLifecycleState.Cancelled;
}

/// <summary>
/// The small, stable six-value lifecycle state from protocol v2 §8 — kept
/// deliberately separate from <see cref="AcquisitionJobStatus"/>, which
/// encodes Family Librarian's own post-fetch staging pipeline, a different
/// concern.
/// </summary>
public enum ProviderAcquisitionJobLifecycleState
{
    Queued,
    Running,
    Waiting,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Protocol v2 §8a's three output kinds.</summary>
public enum ProviderOutputKind
{
    File,
    Uri,
    Descriptor
}

public static class ProviderAcquisitionJobLifecycleTransitions
{
    public const ProviderAcquisitionJobLifecycleState InitialState = ProviderAcquisitionJobLifecycleState.Queued;

    private static readonly Dictionary<ProviderAcquisitionJobLifecycleState, ProviderAcquisitionJobLifecycleState[]> Allowed = new()
    {
        [ProviderAcquisitionJobLifecycleState.Queued] =
        [
            ProviderAcquisitionJobLifecycleState.Queued,
            ProviderAcquisitionJobLifecycleState.Running,
            ProviderAcquisitionJobLifecycleState.Waiting,
            ProviderAcquisitionJobLifecycleState.Completed,
            ProviderAcquisitionJobLifecycleState.Failed,
            ProviderAcquisitionJobLifecycleState.Cancelled
        ],
        [ProviderAcquisitionJobLifecycleState.Running] =
        [
            ProviderAcquisitionJobLifecycleState.Running,
            ProviderAcquisitionJobLifecycleState.Waiting,
            ProviderAcquisitionJobLifecycleState.Completed,
            ProviderAcquisitionJobLifecycleState.Failed,
            ProviderAcquisitionJobLifecycleState.Cancelled
        ],
        [ProviderAcquisitionJobLifecycleState.Waiting] =
        [
            ProviderAcquisitionJobLifecycleState.Running,
            ProviderAcquisitionJobLifecycleState.Waiting,
            ProviderAcquisitionJobLifecycleState.Completed,
            ProviderAcquisitionJobLifecycleState.Failed,
            ProviderAcquisitionJobLifecycleState.Cancelled
        ],
        // Completed, Failed, and Cancelled are terminal -- except that each
        // allows a self-transition. A provider may legitimately report the
        // same terminal state again (a fast provider whose very first status
        // check already reads "completed", or a poll that lands after
        // another has already recorded the same outcome); re-recording the
        // same terminal state must be a safe idempotent no-op, not a thrown
        // transition error, matching protocol v2's idempotency posture
        // throughout the rest of the acquire flow.
        [ProviderAcquisitionJobLifecycleState.Completed] = [ProviderAcquisitionJobLifecycleState.Completed],
        [ProviderAcquisitionJobLifecycleState.Failed] = [ProviderAcquisitionJobLifecycleState.Failed],
        [ProviderAcquisitionJobLifecycleState.Cancelled] = [ProviderAcquisitionJobLifecycleState.Cancelled]
    };

    public static bool IsAllowed(ProviderAcquisitionJobLifecycleState from, ProviderAcquisitionJobLifecycleState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;
}

public sealed class InvalidProviderAcquisitionJobTransitionException(
    ProviderAcquisitionJobLifecycleState from, ProviderAcquisitionJobLifecycleState to)
    : InvalidOperationException($"A provider acquisition job cannot move from {from} to {to}.")
{
    public ProviderAcquisitionJobLifecycleState From { get; } = from;

    public ProviderAcquisitionJobLifecycleState To { get; } = to;
}
