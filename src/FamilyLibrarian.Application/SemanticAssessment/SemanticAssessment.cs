namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>
/// What a model concluded about whether the supplied evidence hangs together. Advisory vocabulary only: none of these
/// values is, or can be converted into, a match, an acceptance, a hold release or any other decision.
/// </summary>
public enum SemanticVerdict
{
    /// <summary>The supplied evidence is consistent with the claimed identity.</summary>
    Consistent = 1,

    /// <summary>The supplied evidence is inconsistent with the claimed identity.</summary>
    Inconsistent = 2,

    /// <summary>The model could not tell, or the evidence does not settle it.</summary>
    Uncertain = 3
}

/// <summary>How a claim bears on the question. Decides which evidence is allowed to back it.</summary>
public enum ClaimStance
{
    Supports = 1,
    Contradicts = 2,

    /// <summary>Raises a concern (incomplete, wrong language, wrong edition) without asserting a contradiction.</summary>
    Concern = 3,

    Neutral = 4
}

/// <summary>One statement the model made, with the supplied evidence ids it relied on. Every id resolved to real evidence.</summary>
public sealed record SemanticClaim(string Kind, ClaimStance Stance, IReadOnlyList<string> EvidenceIds, string Note);

/// <summary>
/// A validated assessment. It holds no decision type, no identifier of anything to act on and no model-reported
/// confidence: it can be shown to an administrator and stored beside the deterministic result, and nothing more.
/// </summary>
public sealed record SemanticAssessment(string SchemaId, SemanticVerdict Verdict, IReadOnlyList<SemanticClaim> Claims);

/// <summary>A versioned output contract: which claim kinds exist and what each one asserts.</summary>
public sealed class SemanticSchema
{
    private readonly Dictionary<string, ClaimStance> kinds;

    private SemanticSchema(string id, Dictionary<string, ClaimStance> kinds)
    {
        Id = id;
        this.kinds = kinds;
    }

    /// <summary>The identifier the model must echo back, for example <c>candidate-assessment/1</c>.</summary>
    public string Id { get; }

    public IReadOnlyCollection<string> Kinds => kinds.Keys;

    public bool TryGetStance(string kind, out ClaimStance stance) => kinds.TryGetValue(kind, out stance);

    /// <summary>Is a candidate release plausibly the requested work, given the supplied evidence?</summary>
    public static SemanticSchema Candidate { get; } = new("candidate-assessment/1", new(StringComparer.Ordinal)
    {
        ["supports_same_work"] = ClaimStance.Supports,
        ["contradicts_same_work"] = ClaimStance.Contradicts,
        ["edition_differs"] = ClaimStance.Concern,
        ["needs_review"] = ClaimStance.Neutral,
        ["insufficient_information"] = ClaimStance.Neutral
    });

    /// <summary>Is a downloaded file plausibly the identity it was staged as, given the recorded evidence?</summary>
    public static SemanticSchema Artifact { get; } = new("artifact-assessment/1", new(StringComparer.Ordinal)
    {
        ["supports_declared_identity"] = ClaimStance.Supports,
        ["contradicts_declared_identity"] = ClaimStance.Contradicts,
        ["suggests_incomplete"] = ClaimStance.Concern,
        ["suggests_wrong_language"] = ClaimStance.Concern,
        ["suggests_wrong_edition"] = ClaimStance.Concern,
        ["needs_review"] = ClaimStance.Neutral,
        ["insufficient_information"] = ClaimStance.Neutral
    });

    /// <summary>The only claim kind allowed to cite no evidence: saying "I cannot tell" needs nothing to back it.</summary>
    public const string InsufficientInformation = "insufficient_information";
}
