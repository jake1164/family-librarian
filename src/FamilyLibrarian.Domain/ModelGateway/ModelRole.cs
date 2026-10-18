namespace FamilyLibrarian.Domain.ModelGateway;

/// <summary>
/// What a model is being asked to do. Each role is configured, enabled and budgeted separately, so turning one on never
/// turns another on. Persisted as text; additive only.
/// </summary>
public enum ModelRole
{
    /// <summary>Interpret a free-text book message after the deterministic parser did not recognise it.</summary>
    IntentInterpretation = 1,

    /// <summary>Phrase a conversational reply. Never decides anything.</summary>
    ConversationResponse = 2,

    /// <summary>Annotate a deterministic <c>Ambiguous</c> or <c>MatchWithConditions</c> candidate result.</summary>
    CandidateAmbiguityAssessment = 3,

    /// <summary>Annotate recorded artifact evidence with a semantic opinion.</summary>
    ArtifactValidationAssessment = 4,

    /// <summary>Identify a book from a cover image. Requires a vision-capable model.</summary>
    VisionCoverIdentification = 5,

    /// <summary>Propose bounded bibliographic searches; never authorize acquisition or change identity verification.</summary>
    SearchGuidance = 6,

    /// <summary>Grounded candidate triage; never bypasses security or artifact validation.</summary>
    CandidateTriage = 7
}

/// <summary>Where a role's model runs. The choice is explicit and there is no automatic fallback from one to the other.</summary>
public enum ModelPlacement
{
    Local = 1,
    Cloud = 2
}
