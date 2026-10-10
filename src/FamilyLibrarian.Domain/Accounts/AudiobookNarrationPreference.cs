namespace FamilyLibrarian.Domain.Accounts;

/// <summary>
/// A household member's own tolerance for computer-generated audiobook
/// narration, applied when Family Librarian must choose among otherwise
/// acceptable audiobook recordings of the same requested work.
/// </summary>
/// <remarks>
/// This is a preference about narration only. It never overrides identity,
/// language, release, or DRM checks, and it is not a general audiobook
/// quality score -- see <c>Application.Acquisition.AudiobookCandidateSelector</c>.
/// </remarks>
public enum AudiobookNarrationPreference
{
    /// <summary>
    /// Prefer a human-narrated recording when one is acceptable, but still
    /// allow a synthetic (computer-generated) recording when it is the only
    /// suitable copy available. The default for every account.
    /// </summary>
    PreferHuman = 1,

    /// <summary>
    /// Never automatically acquire a recording known to use synthetic
    /// narration. A recording whose narration cannot be confirmed is not
    /// treated as satisfying this requirement -- it is routed for review
    /// instead of silently accepted or silently discarded.
    /// </summary>
    HumanOnly = 2,

    /// <summary>Narration type has no effect on candidate selection.</summary>
    NoPreference = 3
}
