namespace FamilyLibrarian.Domain.Evidence;

/// <summary>How a finding bears on identity. Distinguishes unknown from negative on purpose.</summary>
public enum EvidencePolarity
{
    /// <summary>Supports the requested identity.</summary>
    Positive = 1,

    /// <summary>Suggests, but does not establish, the requested identity.</summary>
    Weak = 2,

    /// <summary>An explicit contradiction of the requested identity.</summary>
    Negative = 3,

    /// <summary>No usable evidence either way. Never a contradiction.</summary>
    Unknown = 4,

    /// <summary>A structural property (collection, sample, ...) that needs review.</summary>
    Condition = 5
}

/// <summary>
/// One citable observation about a candidate or artifact.
/// </summary>
/// <param name="Id">
/// Identifier unique and deterministic within one evidence set (for example
/// <c>ev-title</c>, <c>ev-series-1</c>), so a later reviewer or semantic analysis
/// can cite exactly the evidence it was given.
/// </param>
/// <param name="Code">A stable <see cref="EvidenceReasonCodes"/> value.</param>
/// <param name="Polarity">Whether the observation supports, weakens or contradicts the identity.</param>
/// <param name="Text">Human-readable explanation. Advisory; the code is the contract.</param>
public sealed record EvidenceFinding(string Id, string Code, EvidencePolarity Polarity, string Text);
