using System.Globalization;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Providers;

public sealed record SeriesIdentityEvidence(
    IdentityEvidenceState State, string Name, string? ExpectedPosition, string? ObservedPosition, string Reason);

/// <summary>Extracts positive identity and known structure; unclassified text is neutral.</summary>
public static partial class ExternalReleaseNameEvidence
{
    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal)
    {
        "EPUB", "EPUBC", "MOBI", "AZW", "AZW1", "AZW3", "AZW4", "KFX", "KF8", "PRC", "KEPUB",
        "FB2", "FBZ", "LIT", "PDB", "PDF", "RTF", "TXT", "DOC", "DOCX", "DJVU", "CBZ", "CBR",
        "M4B", "M4A", "MP3", "AAC", "OGG", "OGA", "OPUS", "FLAC", "WMA", "ALAC", "ZIP", "RAR", "7Z", "ISO"
    };
    private static readonly HashSet<string> Packaging = new(StringComparer.Ordinal)
    { "BY", "REQ", "RETAIL", "REPACK", "PROPER", "REMASTERED", "EBOOK", "EBOOKS", "AUDIOBOOK", "AUDIOBOOKS", "EDITION", "ANNIVERSARY", "REVISED", "REPRINT" };

    public static ReleaseNameVerdict Evaluate(string? releaseName, IReadOnlyList<string> expectedTitles,
        string? expectedAuthor, IReadOnlyList<BookSeries>? expectedSeries = null)
    {
        ArgumentNullException.ThrowIfNull(expectedTitles);
        if (string.IsNullOrWhiteSpace(releaseName)) return ReleaseNameVerdict.None;
        var part = ExternalAudiobookPartEvidence.Read(releaseName);
        var name = SeparateTrailingFormat(part.Name);
        var tokens = ReleaseTitleMatcher.Tokens(name);
        var consumed = new bool[tokens.Length];
        var title = expectedTitles.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ReleaseTitleMatcher.Evaluate(value, tokens))
            .OrderBy(value => value.State switch { IdentityEvidenceState.Exact => 0, IdentityEvidenceState.Compatible => 1, IdentityEvidenceState.Fuzzy => 2, _ => 3 })
            .FirstOrDefault();
        if (title?.IsPositive == true)
        {
            Mark(title.Start, title.Length);
            // Repeated title labels do not assert another work.
            for (var start = 0; start + title.Length <= tokens.Length; start++)
                if (tokens.Skip(start).Take(title.Length).SequenceEqual(tokens.Skip(title.Start).Take(title.Length)))
                    Mark(start, title.Length);
        }

        string? narrator = null;
        var narration = NarratorCredit().Match(name);
        if (narration.Success)
        {
            narrator = narration.Groups["name"].Value.Trim();
            ConsumePhrase(narration.Value);
        }

        // Credits give name boundaries; arbitrary residual words never become a conflicting author.
        var affinity = AuthorAffinity.Evaluate(expectedAuthor, null);
        var credit = AuthorCredit().Match(name);
        if (credit.Success)
        {
            var creditTokens = ReleaseTitleMatcher.Tokens(credit.Groups["name"].Value);
            var count = creditTokens.Length >= 2 && creditTokens[0].Length == 1 && creditTokens.Length >= 3 ? 3 : Math.Min(2, creditTokens.Length);
            if (count > 0)
            {
                var detected = string.Join(' ', creditTokens.Take(count));
                affinity = AuthorAffinity.Evaluate(expectedAuthor, detected);
                ConsumePhrase(detected);
            }
        }
        if (affinity.Kind == AuthorAffinityKind.Unknown)
        {
            for (var start = 0; start < tokens.Length; start++)
            {
                if (consumed[start]) continue;
                for (var length = Math.Min(3, tokens.Length - start); length >= 1; length--)
                {
                    if (Enumerable.Range(start, length).Any(index => consumed[index])) continue;
                    var detected = string.Join(' ', tokens.Skip(start).Take(length));
                    var support = AuthorAffinity.Evaluate(expectedAuthor, detected);
                    if (support.Kind is AuthorAffinityKind.Conflict or AuthorAffinityKind.Unknown || support.Score <= affinity.Score) continue;
                    affinity = support;
                }
            }
            // Only an explicit, isolated two-word trailing name can contradict without a by credit.
            var tail = TrailingAuthor().Match(name);
            if (!affinity.HasStrongSupport && tail.Success && ReleaseTitleMatcher.Tokens(tail.Groups["name"].Value)
                .Any(token => AuthorAffinity.IsSupportingToken(expectedAuthor, token)))
            {
                var detected = tail.Groups["name"].Value;
                if (!ReleaseTitleMatcher.Tokens(detected).Any(token => Formats.Contains(token)) &&
                    title?.IsPositive == true && !ReleaseTitleMatcher.Tokens(detected).Any(token =>
                        ReleaseTitleMatcher.Tokens(title.Observed ?? string.Empty).Contains(token, StringComparer.Ordinal)))
                    affinity = AuthorAffinity.Evaluate(expectedAuthor, detected);
            }
            if (affinity.Kind != AuthorAffinityKind.Unknown) ConsumePhrase(affinity.DetectedAuthor ?? string.Empty);
        }
        // Keep exact spelling/case when it is available in the raw release.
        if (affinity.DetectedAuthor is { } detectedAuthor)
        {
            var authorRun = string.Join(@"[\W_]+", ReleaseTitleMatcher.Tokens(detectedAuthor).Select(Regex.Escape));
            var observed = Regex.Match(name, authorRun, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (observed.Success) affinity = affinity with { DetectedAuthor = DeterministicBookMatcher.NormalizeWords(observed.Value) };
        }

        var seriesEvidence = new List<SeriesIdentityEvidence>();
        foreach (var series in expectedSeries ?? [])
        {
            var run = ReleaseTitleMatcher.Evaluate(series.Name, tokens);
            if (!run.IsPositive) continue;
            Mark(run.Start, run.Length);
            var pattern = string.Join(@"[\W_]+", tokens.Skip(run.Start).Take(run.Length).Select(Regex.Escape));
            var position = Regex.Match(name, $@"(?<![\p{{L}}\p{{N}}]){pattern}[\W_]+(?:BOOK[\W_]+)?(?<position>\d+(?:\.\d+)?)(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var observed = position.Success ? position.Groups["position"].Value : null;
            var comparable = !string.IsNullOrWhiteSpace(series.Position) && observed is not null;
            var state = !comparable ? IdentityEvidenceState.Unknown : PositionsEqual(series.Position!, observed!)
                ? IdentityEvidenceState.Exact : IdentityEvidenceState.Conflicting;
            seriesEvidence.Add(new(state, series.Name, series.Position, observed,
                comparable ? "Comparable catalog and release series positions." : "A missing position is not disagreement."));
            if (observed is not null) Mark(run.Start + run.Length, Math.Min(ReleaseTitleMatcher.Tokens(observed).Length, tokens.Length - run.Start - run.Length));
        }
        if (seriesEvidence.Count == 0 && title?.IsPositive == true && title.Start > 0)
        {
            var titlePattern = string.Join(@"[\W_]+", tokens.Skip(title.Start).Take(title.Length).Select(Regex.Escape));
            var titleMatch = Regex.Match(name, titlePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var prefix = titleMatch.Success ? name[..titleMatch.Index] : string.Empty;
            if (!string.IsNullOrWhiteSpace(affinity.DetectedAuthor))
            {
                var authorPattern = string.Join(@"[\W_]+", ReleaseTitleMatcher.Tokens(affinity.DetectedAuthor).Select(Regex.Escape));
                prefix = Regex.Replace(prefix, authorPattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            var inferred = SeriesPrefix().Match(prefix.Trim(' ', '.', '-', '[', ']', '_'));
            if (inferred.Success)
            {
                var seriesName = DeterministicBookMatcher.NormalizeWords(inferred.Groups["name"].Value);
                var position = inferred.Groups["position"].Value;
                seriesEvidence.Add(new(IdentityEvidenceState.Unknown, seriesName, null, position,
                    "Release series prefix retained; requested comparable position is unknown."));
                ConsumePhrase(inferred.Value);
            }
        }
        // Book N is comparable only with one known series position. It is not a part marker.
        var bookNumber = BookNumber().Match(name);
        if (bookNumber.Success)
        {
            var observed = bookNumber.Groups["position"].Value;
            var expected = expectedSeries is { Count: 1 } ? expectedSeries[0].Position : null;
            var state = string.IsNullOrWhiteSpace(expected) ? IdentityEvidenceState.Unknown :
                PositionsEqual(expected, observed) ? IdentityEvidenceState.Exact : IdentityEvidenceState.Conflicting;
            seriesEvidence.Add(new(state, expectedSeries is { Count: 1 } ? expectedSeries[0].Name : "Unspecified series", expected, observed, "Explicit book-number packaging."));
            ConsumePhrase(bookNumber.Value);
        }

        var qualifiers = StructuralQualifiers().Matches(name).Select(match =>
            DeterministicBookMatcher.NormalizeWords(match.Value).ToUpperInvariant()).Distinct(StringComparer.Ordinal)
            .Where(value => title?.Observed is null || !(" " + title.Observed + " ").Contains(" " + value + " ", StringComparison.Ordinal)).ToArray();
        var conditions = qualifiers.Where(value => value != "UNABRIDGED").Select(value => new ReleaseCondition(ReleaseConditionKind.EditionReview, $"Release qualifier requires completeness or edition review: {value}.")).ToList();
        if (part.Name.Contains('/', StringComparison.Ordinal) || Regex.IsMatch(name, @"\s&\s", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            conditions.Add(new(ReleaseConditionKind.EditionReview, "Combined-work or slash-separated release needs review."));
        if (part.Evidence is { Total: not 1 } fragment) conditions.Add(new(ReleaseConditionKind.CompanionParts, $"{fragment.Description}: companion completeness must be resolved."));
        if (part.Evidence is null && PartLikeMarker().IsMatch(name)) conditions.Add(new(ReleaseConditionKind.MalformedStructure, "Part numbering is malformed or ambiguous."));
        if (BookLikeMarker().IsMatch(name) && !bookNumber.Success) conditions.Add(new(ReleaseConditionKind.MalformedStructure, "Book-number packaging is not interpretable."));
        foreach (var qualifier in qualifiers) ConsumePhrase(qualifier);

        string? language = null;
        string? format = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (consumed[index]) continue;
            var token = tokens[index];
            // Brackets do not hide contradictory language or edition assertions.
            if (LanguageAcceptance.TryResolveLanguageName(token, out var resolved)) { language ??= resolved; consumed[index] = true; }
            else if (Formats.Contains(token)) { format ??= token.ToLowerInvariant(); consumed[index] = true; }
            else if (Packaging.Contains(token) || Regex.IsMatch(token, @"^(?:\d{1,3}(?:ST|ND|RD|TH)|(?:1[4-9]|20)\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) consumed[index] = true;
            else if (token == "S" && index > 0 && consumed[index - 1]) consumed[index] = true;
        }
        var contradiction = seriesEvidence.FirstOrDefault(value => value.State == IdentityEvidenceState.Conflicting);
        return new(title?.IsPositive == true, affinity.HasStrongSupport, language, narrator, format,
            tokens.Where((_, index) => !consumed[index]).ToArray(),
            contradiction is null ? null : $"Series position conflicts: requested {contradiction.ExpectedPosition}, observed {contradiction.ObservedPosition}.",
            affinity, Part: part.Evidence, TitleEvidence: title, SeriesEvidence: seriesEvidence,
            Conditions: conditions, StructuralQualifiers: qualifiers,
            RawReleaseTitle: releaseName, NormalizedRelease: string.Join(' ', ReleaseTitleMatcher.Tokens(releaseName)),
            Tokens: ReleaseTitleMatcher.Tokens(releaseName),
            ReleaseBase: title?.IsPositive == true ? string.Join(' ', tokens.Take(title.Start)
                .Where(token => !AuthorAffinity.IsSupportingToken(expectedAuthor, token) && token != "REQ")
                .Concat(tokens.Skip(title.Start).Take(title.Length))) : null);

        void Mark(int start, int length) { for (var index = start; index < start + length; index++) consumed[index] = true; }
        void ConsumePhrase(string value)
        {
            var run = ReleaseTitleMatcher.Tokens(value);
            for (var start = 0; run.Length > 0 && start + run.Length <= tokens.Length; start++)
                if (run.SequenceEqual(tokens.Skip(start).Take(run.Length))) Mark(start, run.Length);
        }
    }

    internal static bool PositionsEqual(string left, string right) =>
        decimal.TryParse(left, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var l) &&
        decimal.TryParse(right, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r)
            ? l == r : string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string SeparateTrailingFormat(string name)
    {
        var trimmed = name.TrimEnd();
        foreach (var suffix in Formats.OrderByDescending(token => token.Length))
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var start = trimmed.Length - suffix.Length;
            return start > 0 && char.IsLetterOrDigit(trimmed[start - 1]) ? trimmed.Insert(start, " ") : name;
        }
        return name;
    }

    [GeneratedRegex(@"^(?<name>[\p{L}]+(?:[. _-]+[\p{L}]+){0,7})[. _-]+\[?(?<position>\d+(?:\.\d+)?)\]?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SeriesPrefix();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:READ|NARRATED|PERFORMED)[\W_]+BY[\W_]+(?<name>[\p{L}]+(?:[ .]+[\p{L}]+)*)(?:[)\]]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NarratorCredit();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<!READ[. ])(?<!NARRATED[. ])(?<!PERFORMED[. ])BY[\W_]+(?<name>[\p{L}]+(?:[\W_]+[\p{L}]+){0,2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AuthorCredit();
    [GeneratedRegex(@"\s[-–—]\s(?<name>[\p{L}]+[ .]+[\p{L}]+)\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingAuthor();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])BOOK[\W_]+(?<position>[1-9]\d{0,3})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BookNumber();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:SUMMARY[\W_]+OF|STUDY[\W_]+GUIDE|COMPANION[\W_]+TO|WORKBOOK[\W_]+FOR|ANALYSIS[\W_]+OF|CLIFFSNOTES|CLIFF[\W_]+NOTES|SPARKNOTES|EXCERPT|SAMPLE(?:[\W_]+CHAPTER)?|PREVIEW|ABRIDGED|UNABRIDGED|OMNIBUS|BOX(?:ED)?[\W_]+SET|COLLECTION|STORIES|BOOKS[\W_]+\d+[\W_]+\d+|GRAPHICAUDIO|GRAPHIC[\W_]+NOVEL|DRAMATIZED|FULL[\W_]+CAST|RADIO[\W_]+(?:AUDIO[\W_]+)?DRAMA|EXTENDED)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StructuralQualifiers();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:PART|PT|DISC|CD)[\W_]+\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PartLikeMarker();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])BOOK[\W_]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BookLikeMarker();
}

public sealed record ReleaseNameVerdict(
    bool AssertsExpectedTitle, bool AssertsExpectedAuthor,
    string? AssertedLanguage, string? AssertedNarrator, string? AssertedFormat,
    IReadOnlyList<string> UnexplainedTokens, string? RejectionReason,
    AuthorAffinityResult? AuthorAffinity = null, bool StructuredTitlePlausible = false,
    ExternalAudiobookPartEvidence? Part = null,
    ReleaseTitleEvidence? TitleEvidence = null, IReadOnlyList<SeriesIdentityEvidence>? SeriesEvidence = null,
    IReadOnlyList<ReleaseCondition>? Conditions = null, IReadOnlyList<string>? StructuralQualifiers = null,
    string? RawReleaseTitle = null, string? NormalizedRelease = null, IReadOnlyList<string>? Tokens = null, string? ReleaseBase = null)
{
    public static readonly ReleaseNameVerdict None = new(false, false, null, null, null, [], null);
    public bool IsStrictWorkAssertion => AssertsExpectedTitle && AuthorAffinity?.Kind != AuthorAffinityKind.Conflict &&
        RejectionReason is null && (Conditions?.Count ?? 0) == 0 &&
        (TitleEvidence?.State != IdentityEvidenceState.Fuzzy || AuthorAffinity?.HasStrongSupport == true);
}
