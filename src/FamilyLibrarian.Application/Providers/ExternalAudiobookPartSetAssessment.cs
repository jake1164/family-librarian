using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Companion availability within one work's search. This is review evidence,
/// not authorization to download fragments or a promise of identical editions.
/// </summary>
public sealed record ExternalAudiobookPartSetAssessment(
    IReadOnlyList<FulfillmentOption> Parts, IReadOnlyList<int> MissingParts, bool HasAmbiguousParts)
{
    public bool HasEveryNumber => Parts.Count > 0 && MissingParts.Count == 0 && !HasAmbiguousParts;

    public static ExternalAudiobookPartSetAssessment For(
        FulfillmentOption fragment, IReadOnlyList<FulfillmentOption> candidates)
    {
        var part = fragment.AudiobookPart;
        if (part?.Total is not { } total)
            return new([], [], false);
        var compatible = candidates.Where(candidate => candidate.AudiobookPart?.Total == total &&
            candidate.HasPlausibleTitle && candidate.AuthorAffinity?.Kind != AuthorAffinityKind.Conflict &&
            candidate.ProviderId == fragment.ProviderId &&
            Compatible(candidate.Format, fragment.Format) && Compatible(candidate.Language, fragment.Language) &&
            Compatible(candidate.Narrator, fragment.Narrator) &&
            !(candidate.IsAbridged == true && fragment.IsUnabridged == true) &&
            !(fragment.IsAbridged == true && candidate.IsUnabridged == true) &&
            EditionMarkers(candidate.ReleaseName) == EditionMarkers(fragment.ReleaseName)).ToArray();
        var numbers = compatible.Select(candidate => candidate.AudiobookPart!.Number).ToHashSet();
        return new(compatible.OrderBy(candidate => candidate.AudiobookPart!.Number)
            .ThenBy(candidate => candidate.ProviderResultId, StringComparer.Ordinal).ToArray(),
            Enumerable.Range(1, total).Where(number => !numbers.Contains(number)).ToArray(),
            compatible.GroupBy(candidate => candidate.AudiobookPart!.Number).Any(group => group.Count() > 1));
    }

    public string Description => Parts.Count == 0
        ? "No compatible complete set is established, so completeness cannot be verified."
        : HasAmbiguousParts
            ? "Multiple possible copies of a numbered part were found; the set needs an edition comparison."
            : MissingParts.Count > 0
                ? $"Missing compatible part numbers in this search: {string.Join(", ", MissingParts)}."
                : "Every numbered part is present in this search. Verify that they belong to the same audiobook edition; these remain separate source records.";

    private static bool Compatible(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
        string.Equals(DeterministicBookMatcher.NormalizeWords(left), DeterministicBookMatcher.NormalizeWords(right),
            StringComparison.OrdinalIgnoreCase);

    private static string EditionMarkers(string? releaseName)
    {
        var words = DeterministicBookMatcher.NormalizeWords(releaseName ?? string.Empty)
            .ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Where(word => word is "GRAPHICAUDIO" or "DRAMATIZED" or "ABRIDGED" or
            "UNABRIDGED" or "EXTENDED").Distinct().Order(StringComparer.Ordinal));
    }
}
