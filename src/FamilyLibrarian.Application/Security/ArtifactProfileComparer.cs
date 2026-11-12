using System.Globalization;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Security;

public static class ArtifactProfileComparer
{
    public const decimal PeerOutlierRatio = 0.30m;

    public static IReadOnlyList<IdentityFact> Compare(IReadOnlyList<IdentityFact> facts, ExpectedArtifactProfile expected)
    {
        var comparisons = CompareCatalogProfile(facts, expected);
        comparisons.AddRange(ComparePeerSize(expected));
        return comparisons;
    }

    private static IReadOnlyList<IdentityFact> ComparePeerSize(ExpectedArtifactProfile expected)
    {
        if (expected.PeerCount is not >= 3 || expected.PeerMedianSizeBytes is not > 0 ||
            expected.AssetSizeBytes is not >= 0 || expected.AssetSizeBytes.Value >= expected.PeerMedianSizeBytes.Value * PeerOutlierRatio)
            return [];
        return [new("ev-profile-peer-size", EvidenceDimension.Completeness, EvidenceReasonCodes.ProfileComparison.SizePeerOutlier,
            EvidencePolarity.Weak, string.Create(CultureInfo.InvariantCulture,
                $"bytes={expected.AssetSizeBytes};peer_median_bytes={expected.PeerMedianSizeBytes};peers={expected.PeerCount}"))];
    }

    public const int WordsPerPage = 250;
    public const int MinimumPagesForLengthHold = 80;
    public const decimal FarBelowFraction = 0.25m;
    public const decimal ShortTextFraction = 0.60m;
    public const decimal ShortAudioFraction = 0.70m;
    public const int MinimumMissingTocTargets = 2;
    public const decimal MissingTocFraction = 0.10m;

    private static List<IdentityFact> CompareCatalogProfile(IReadOnlyList<IdentityFact> facts, ExpectedArtifactProfile expected)
    {
        var comparisons = new List<IdentityFact>();
        var sequence = 0;
        foreach (var fact in facts)
        {
            if (fact.Code == EvidenceReasonCodes.ArtifactProfile.PackageSeries &&
                ComparableSeries(fact.Value, expected.Series) is { } series)
            {
                var agrees = ExternalReleaseNameEvidence.PositionsEqual(series.Expected, series.Actual);
                comparisons.Add(Fact($"ev-compare-series-{++sequence}", agrees
                    ? EvidenceReasonCodes.ProfileComparison.SeriesPositionAgrees : EvidenceReasonCodes.ProfileComparison.SeriesPositionDiffers,
                    agrees ? EvidencePolarity.Positive : EvidencePolarity.Negative, fact.Value));
            }
            if (fact.Code == EvidenceReasonCodes.ArtifactProfile.PackageIdentifier &&
                IsKnownIsbn13(fact.Value, expected.Isbn13s))
                comparisons.Add(Fact($"ev-compare-isbn-{++sequence}", EvidenceReasonCodes.ProfileComparison.IdentifierAgrees,
                    EvidencePolarity.Positive, fact.Value));
            if (fact.Code == EvidenceReasonCodes.ArtifactProfile.TextWordCount && expected.CatalogPageCount is > 0 &&
                TryNumber(fact.Value, "words", out var words))
            {
                var ratio = words / (expected.CatalogPageCount.Value * (decimal)WordsPerPage);
                var clear = ratio < FarBelowFraction && expected.CatalogPageCount >= MinimumPagesForLengthHold;
                if (clear || ratio < ShortTextFraction || expected.CatalogPageCount < MinimumPagesForLengthHold && ratio < 1)
                    comparisons.Add(Fact("ev-compare-length", clear ? EvidenceReasonCodes.ProfileComparison.LengthFarBelowExpected
                        : EvidenceReasonCodes.ProfileComparison.LengthBelowExpected, clear ? EvidencePolarity.Negative : EvidencePolarity.Weak,
                        $"words={words};pages={expected.CatalogPageCount};ratio={ratio.ToString("0.####", CultureInfo.InvariantCulture)}"));
            }
            if (fact.Code == EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration && expected.CatalogAudioSeconds is > 0 &&
                TryNumber(fact.Value, "seconds", out var seconds))
            {
                var ratio = seconds / expected.CatalogAudioSeconds.Value;
                if (ratio < ShortAudioFraction)
                    comparisons.Add(Fact("ev-compare-duration", ratio < FarBelowFraction
                        ? EvidenceReasonCodes.ProfileComparison.DurationFarBelowExpected : EvidenceReasonCodes.ProfileComparison.DurationBelowExpected,
                        ratio < FarBelowFraction ? EvidencePolarity.Negative : EvidencePolarity.Weak,
                        $"seconds={seconds};expected={expected.CatalogAudioSeconds}"));
            }
            if (fact.Code == EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing)
                comparisons.Add(Fact("ev-compare-toc", EvidenceReasonCodes.ProfileComparison.TocEntriesMissing,
                    EvidencePolarity.Condition, fact.Value));
            if (fact.Code is EvidenceReasonCodes.ArtifactProfile.Heading or EvidenceReasonCodes.ArtifactProfile.TocEntry &&
                FindAbridgedMarker(fact.Value) is { } marker)
                comparisons.Add(Fact($"ev-compare-abridged-{++sequence}", EvidenceReasonCodes.ProfileComparison.AbridgedMarker,
                    EvidencePolarity.Condition, marker));
        }

        var headings = facts.Where(f => f.Code == EvidenceReasonCodes.ArtifactProfile.Heading).ToArray();
        if (headings.Length >= 5 && headings.Count(f => HasOtherLanguageCue(f.Value, expected.AcceptedLanguage)) >= headings.Length * 0.60m)
            comparisons.Add(Fact("ev-compare-heading-language", EvidenceReasonCodes.ProfileComparison.HeadingsOtherLanguage,
                EvidencePolarity.Weak, "at least 60% of headings have another language cue"));
        return comparisons;
    }

    public static bool HasCorroboration(IEnumerable<IdentityFact> facts, IReadOnlyList<BookSeries> series, IReadOnlyList<string> isbn13s) =>
        facts.Any(f => f.Code == EvidenceReasonCodes.ArtifactProfile.PackageIdentifier && IsKnownIsbn13(f.Value, isbn13s) ||
            f.Code == EvidenceReasonCodes.ArtifactProfile.PackageSeries && ComparableSeries(f.Value, series) is { } pair &&
            ExternalReleaseNameEvidence.PositionsEqual(pair.Expected, pair.Actual));

    public static bool MissingTocMeetsHoldThreshold(string? value)
    {
        var parts = value?.Split('/');
        return parts is { Length: 2 } && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var missing) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0 && missing <= total &&
            missing >= MinimumMissingTocTargets && missing / (decimal)total >= MissingTocFraction;
    }

    public static string? FindAbridgedMarker(string? text) => string.IsNullOrWhiteSpace(text) ? null :
        DeterministicBookMatcher.NormalizeWords(text[..Math.Min(text.Length, 4096)]).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(w => w.Equals("abridged", StringComparison.OrdinalIgnoreCase) ||
                w.Equals("gekürzt", StringComparison.OrdinalIgnoreCase) || w.Equals("abrégé", StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeIsbn13(string? value)
    {
        var text = value?.Trim();
        if (text is null) return null;
        if (text.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)) text = text[9..];
        text = text.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return text.Length == 13 && text.All(c => c is >= '0' and <= '9') ? text : null;
    }

    private static bool IsKnownIsbn13(string? value, IReadOnlyList<string> known) => NormalizeIsbn13(value) is { } isbn &&
        known.Any(candidate => NormalizeIsbn13(candidate) == isbn);

    private static (string Expected, string Actual)? ComparableSeries(string? value, IReadOnlyList<BookSeries> series)
    {
        var separator = value?.LastIndexOf('#') ?? -1;
        if (value is null || separator <= 0 || separator == value.Length - 1) return null;
        var name = CatalogText.NormalizeForMatch(value[..separator]);
        var expected = series.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Position) &&
            CatalogText.NormalizeForMatch(s.Name) == name);
        return expected is null ? null : (expected.Position!, value[(separator + 1)..]);
    }

    private static bool TryNumber(string? value, string key, out decimal number)
    {
        var token = value?.Split(';').FirstOrDefault(p => p.StartsWith(key + "=", StringComparison.Ordinal));
        number = 0;
        return token is not null && decimal.TryParse(token[(key.Length + 1)..], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out number) && number >= 0;
    }

    private static bool HasOtherLanguageCue(string? value, string? acceptedLanguage)
    {
        var tokens = DeterministicBookMatcher.NormalizeWords(value ?? "").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return LanguageCueWords.TryFind(tokens, new bool[tokens.Length], out var language, out _, out _) &&
            !LanguageAcceptance.IsAcceptedOrUnspecified(language, acceptedLanguage);
    }

    private static IdentityFact Fact(string id, string code, EvidencePolarity polarity, string? value) =>
        new(id, EvidenceDimension.Profile, code, polarity, ArtifactEvidence.Bound(value));
}
