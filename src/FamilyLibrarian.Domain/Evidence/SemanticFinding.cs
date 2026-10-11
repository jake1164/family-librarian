using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Domain.Evidence;

public enum SemanticSubjectKind
{
    Candidate = 1,
    Artifact = 2
}

/// <summary>
/// A model's advisory opinion about one set of evidence, stored <em>beside</em> the deterministic result and never inside
/// it (alpha.6 M4-3). It is keyed by a digest of the exact evidence it was shown, so it can be reused for identical
/// evidence and is recognisably stale when the evidence changes.
/// </summary>
/// <remarks>
/// A finding records what the model said and how the call ended, including failures (a model that returns bad JSON is
/// useful to know about). It never records a prompt, a reply verbatim, an excerpt or a key: only the validated verdict and
/// claims, which cite evidence ids and carry short plain notes. Nothing reads a finding to make a decision.
/// </remarks>
public sealed class SemanticFinding
{
    public const int MaxClaimsJsonLength = 4_000;
    public const int MaxDetailLength = 200;
    public const int MaxReferenceLength = 256;

    private SemanticFinding()
    {
    }

    public SemanticFinding(
        SemanticSubjectKind kind,
        string subjectReference,
        Guid? requestId,
        string? providerId,
        string schemaId,
        string evidenceDigest,
        string deterministicDecision,
        ModelRole role,
        string model,
        bool isCloud,
        ModelCallOutcome outcome,
        string? verdict,
        string? claimsJson,
        string? failureDetail,
        DateTimeOffset createdAtUtc)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(deterministicDecision);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (outcome == ModelCallOutcome.Succeeded != (verdict is not null && claimsJson is not null))
        {
            throw new ArgumentException("A finding has a verdict and claims exactly when the call succeeded.");
        }

        Id = Guid.NewGuid();
        Kind = kind;
        SubjectReference = Clip(subjectReference, MaxReferenceLength)!;
        RequestId = requestId;
        ProviderId = Clip(providerId, 64);
        SchemaId = Clip(schemaId, 64)!;
        EvidenceDigest = evidenceDigest.Trim();
        DeterministicDecision = Clip(deterministicDecision, 48)!;
        Role = role;
        Model = Clip(model, ModelGatewaySettings.MaxModelNameLength)!;
        IsCloud = isCloud;
        Outcome = outcome;
        Verdict = Clip(verdict, 32);
        ClaimsJson = claimsJson is { Length: > MaxClaimsJsonLength } ? throw new ArgumentException("Claims are too large to store.") : claimsJson;
        FailureDetail = Clip(failureDetail, MaxDetailLength);
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public SemanticSubjectKind Kind { get; private set; }

    /// <summary>What the finding is about: a provider reference for a candidate, an asset id for an artifact.</summary>
    public string SubjectReference { get; private set; } = string.Empty;

    public Guid? RequestId { get; private set; }

    public string? ProviderId { get; private set; }

    public string SchemaId { get; private set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the exact evidence the model was shown.</summary>
    public string EvidenceDigest { get; private set; } = string.Empty;

    /// <summary>What the fixed rules concluded when this was asked. Recorded for comparison only.</summary>
    public string DeterministicDecision { get; private set; } = string.Empty;

    public ModelRole Role { get; private set; }

    public string Model { get; private set; } = string.Empty;

    public bool IsCloud { get; private set; }

    public ModelCallOutcome Outcome { get; private set; }

    /// <summary>The model's advisory verdict text (<c>Consistent</c>, <c>Inconsistent</c>, <c>Uncertain</c>); null unless the call succeeded.</summary>
    public string? Verdict { get; private set; }

    /// <summary>The validated claims as JSON; null unless the call succeeded.</summary>
    public string? ClaimsJson { get; private set; }

    public string? FailureDetail { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Whether the model gave a usable answer. Anything else is a recorded failure, not a verdict.</summary>
    public bool IsUsable => Outcome == ModelCallOutcome.Succeeded;

    /// <summary>
    /// Whether an identical question is worth asking again. A provider or model problem may pass; a model that answered
    /// badly will answer the same way for the same evidence, so that result is reused rather than re-bought. Hitting the output
    /// limit is not reused: it depends on settings (the cap, reasoning) an administrator can change.
    /// </summary>
    public bool IsReusable => Outcome is ModelCallOutcome.Succeeded or ModelCallOutcome.EmptyContent or ModelCallOutcome.InvalidJson
        or ModelCallOutcome.SchemaRejected or ModelCallOutcome.OutputTooLarge;

    private static string? Clip(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
