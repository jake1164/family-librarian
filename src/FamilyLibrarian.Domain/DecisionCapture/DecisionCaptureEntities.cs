using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Domain.DecisionCapture;

/// <summary>The kinds of observed fact a decision snapshot can be linked to. Facts only; S5 turns them into expected answers.</summary>
public enum DecisionOutcomeKind
{
    FileApproved = 1,
    FileRejected = 2,
    IdentityOverridden = 3,
    FileDiscarded = 4,
    ModelHoldApplied = 5,
    ModelReleaseApplied = 6,
    CandidateDownloadedVerified = 7,
    VerificationFailed = 8,
    RequesterDeclined = 9,
    SearchFoundSelectedResult = 10,
    SearchReturnedNoResults = 11
}

public enum DecisionOutcomeStrength
{
    Weak = 1,
    Strong = 2
}

/// <summary>
/// One real decision's allow-listed input, kept so the same decision can later be re-run against another model without
/// contacting any source. Never stores book excerpts, file names, hashes, storage references, provider data or tokens.
/// Its digest is its own, not the production reuse digest. Tables only in C1-a: nothing writes a snapshot yet.
/// </summary>
public sealed class DecisionSnapshot
{
    public const int MaxReferenceLength = 256;
    public const int MaxDigestLength = 64;
    public const int MaxStaleReasonLength = 64;

    private DecisionSnapshot()
    {
    }

    public DecisionSnapshot(
        ModelRole role, int schemaVersion, string subjectReference, string snapshotDigest, string snapshotJson,
        Guid? requestId, Guid? userId, Guid? mediaAssetId, bool excerptsInvolved, DateTimeOffset seenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        if (System.Text.Encoding.UTF8.GetByteCount(snapshotJson) > DecisionCaptureLimits.MaxSnapshotBytes)
        {
            throw new ArgumentException("The snapshot is larger than the size guard and must not be stored.", nameof(snapshotJson));
        }

        Id = Guid.NewGuid();
        Role = role;
        SchemaVersion = schemaVersion;
        SubjectReference = subjectReference.Length <= MaxReferenceLength ? subjectReference : subjectReference[..MaxReferenceLength];
        SnapshotDigest = snapshotDigest.Length <= MaxDigestLength ? snapshotDigest : snapshotDigest[..MaxDigestLength];
        SnapshotJson = snapshotJson;
        RequestId = requestId;
        UserId = userId;
        MediaAssetId = mediaAssetId;
        ExcerptsInvolved = excerptsInvolved;
        FirstSeenAtUtc = seenAtUtc;
        LastSeenAtUtc = seenAtUtc;
        TimesSeen = 1;
    }

    public Guid Id { get; private set; }

    public ModelRole Role { get; private set; }

    public int SchemaVersion { get; private set; }

    /// <summary>Same convention as the semantic finding's subject reference.</summary>
    public string SubjectReference { get; private set; } = "";

    public string SnapshotDigest { get; private set; } = "";

    public string SnapshotJson { get; private set; } = "";

    /// <summary>Cascades when the request is deleted. Not a link to an asset, because assets are kept.</summary>
    public Guid? RequestId { get; private set; }

    /// <summary>Intent only; cascades when the user is deleted.</summary>
    public Guid? UserId { get; private set; }

    public Guid? MediaAssetId { get; private set; }

    /// <summary>True when book excerpts were sent to the model. The excerpts themselves are never stored.</summary>
    public bool ExcerptsInvolved { get; private set; }

    public DateTimeOffset FirstSeenAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public int TimesSeen { get; private set; }

    public string? StaleReason { get; private set; }

    public void MarkSeen(DateTimeOffset seenAtUtc)
    {
        TimesSeen++;
        if (seenAtUtc > LastSeenAtUtc) LastSeenAtUtc = seenAtUtc;
    }
}

/// <summary>
/// One real call made against a snapshot, with the exact run identity. Replies are not stored; the validated claims live in the
/// finding, and a replay produces its own reply.
/// </summary>
public sealed class DecisionCall
{
    public const int MaxHashLength = 80;
    public const int MaxAnswerLength = 200;

    private DecisionCall()
    {
    }

    public DecisionCall(
        Guid snapshotId, Guid? modelUsageRecordId, Guid? semanticFindingId, Guid? settingsId, ModelPlacement placement,
        string modelAlias, string? reportedModel, ModelReasoningMode reasoning, string renderedMessagesHash, string parserVersion,
        ModelCallOutcome outcome, ModelFailureReason? failureReason, string? answer, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderedMessagesHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        ModelUsageRecordId = modelUsageRecordId;
        SemanticFindingId = semanticFindingId;
        SettingsId = settingsId;
        Placement = placement;
        ModelAlias = modelAlias.Length <= ModelGatewaySettings.MaxModelNameLength ? modelAlias : modelAlias[..ModelGatewaySettings.MaxModelNameLength];
        ReportedModel = reportedModel is { Length: > ModelGatewaySettings.MaxModelNameLength } oversized ? oversized[..ModelGatewaySettings.MaxModelNameLength] : reportedModel;
        Reasoning = reasoning;
        RenderedMessagesHash = renderedMessagesHash.Length <= MaxHashLength ? renderedMessagesHash : renderedMessagesHash[..MaxHashLength];
        ParserVersion = parserVersion.Length <= 64 ? parserVersion : parserVersion[..64];
        Outcome = outcome;
        FailureReason = failureReason;
        Answer = answer is { Length: > MaxAnswerLength } text ? text[..MaxAnswerLength] : answer;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid SnapshotId { get; private set; }

    /// <summary>No foreign key: the usage ledger is pruned at 90 days.</summary>
    public Guid? ModelUsageRecordId { get; private set; }

    public Guid? SemanticFindingId { get; private set; }

    /// <summary>The gateway settings row plus <see cref="Placement"/> identify the connection (there is one Local and one Cloud).</summary>
    public Guid? SettingsId { get; private set; }

    public ModelPlacement Placement { get; private set; }

    public string ModelAlias { get; private set; } = "";

    /// <summary>The model name the provider said answered, which can differ from the alias.</summary>
    public string? ReportedModel { get; private set; }

    public ModelReasoningMode Reasoning { get; private set; }

    /// <summary>Version-tagged hash of the exact messages, JSON mode and output cap that were sent.</summary>
    public string RenderedMessagesHash { get; private set; } = "";

    public string ParserVersion { get; private set; } = "";

    public ModelCallOutcome Outcome { get; private set; }

    public ModelFailureReason? FailureReason { get; private set; }

    public string? Answer { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}

/// <summary>An observed fact about a decision's subject. Silence is never recorded as an outcome.</summary>
public sealed class DecisionOutcome
{
    private DecisionOutcome()
    {
    }

    public DecisionOutcome(
        Guid snapshotId, string? candidateKey, DecisionOutcomeKind kind, string source, bool influencedByModelNote,
        DecisionOutcomeStrength strength, DateTimeOffset observedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(strength)) throw new ArgumentOutOfRangeException(nameof(strength));
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        CandidateKey = candidateKey;
        Kind = kind;
        Source = source.Length <= 48 ? source : source[..48];
        InfluencedByModelNote = influencedByModelNote;
        Strength = strength;
        ObservedAtUtc = observedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid SnapshotId { get; private set; }

    public string? CandidateKey { get; private set; }

    public DecisionOutcomeKind Kind { get; private set; }

    public string Source { get; private set; } = "";

    /// <summary>Set when the administrator could see the model's note before deciding, so labels are known to favour that model.</summary>
    public bool InfluencedByModelNote { get; private set; }

    public DecisionOutcomeStrength Strength { get; private set; }

    public DateTimeOffset ObservedAtUtc { get; private set; }
}

/// <summary>
/// Maps a snapshot's local candidate key to the provider ids needed to attach an outcome to the right candidate. Kept outside the
/// snapshot JSON, never part of replay input, and removed with the snapshot.
/// </summary>
public sealed class CandidateLink
{
    private CandidateLink()
    {
    }

    public CandidateLink(Guid snapshotId, string candidateKey, string providerId, string providerResultId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerResultId);
        SnapshotId = snapshotId;
        CandidateKey = candidateKey.Length <= 64 ? candidateKey : candidateKey[..64];
        ProviderId = providerId.Length <= 64 ? providerId : providerId[..64];
        ProviderResultId = providerResultId.Length <= 256 ? providerResultId : providerResultId[..256];
    }

    public Guid SnapshotId { get; private set; }

    public string CandidateKey { get; private set; } = "";

    public string ProviderId { get; private set; } = "";

    public string ProviderResultId { get; private set; } = "";
}
