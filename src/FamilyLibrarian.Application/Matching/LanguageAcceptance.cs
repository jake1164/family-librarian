namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The hardcoded English-only language policy for automatic matching (see
/// .ai_docs/family-librarian-accuracy-selfservice-alpha2-plan.md, ACCURACY-1).
/// Full multi-language/household-preference support is a longer-term goal,
/// not this.
/// </summary>
public static class LanguageAcceptance
{
    /// <summary>
    /// True when <paramref name="language"/> is missing (treated as
    /// unspecified, not rejected -- rejecting on missing metadata would hurt
    /// automation more than it helps accuracy) or declares English in any of
    /// its common forms (<c>en</c>, <c>eng</c>, <c>en-US</c>, <c>English</c>).
    /// </summary>
    public static bool IsEnglishOrUnspecified(string? language) =>
        string.IsNullOrWhiteSpace(language) ||
        language.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);
}
