using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

public static class BookCandidateGrouper
{
    public static IReadOnlyList<BookCandidate> GroupMatchingCandidates(
        IEnumerable<BookCandidate> candidates) =>
        GroupMatchingCandidates(candidates, null);

    public static IReadOnlyList<BookCandidate> GroupMatchingCandidates(
        IEnumerable<BookCandidate> candidates,
        string? searchText)
    {
        var groupedCandidates = candidates
            .GroupBy(GetMatchKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group
                    .OrderByDescending(GetCompletenessScore)
                    .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.ExternalId, StringComparer.Ordinal)
                    .ToArray();

                // The most complete candidate represents the group in the results
                // list, but every provider that reported this same work is kept
                // as a merged source so the UI can still link out to each of them.
                var sources = ordered
                    .Select(candidate => new BookCandidateSource(
                        candidate.ProviderId,
                        candidate.ProviderName,
                        candidate.ExternalId,
                        candidate.SourceUrl))
                    .ToArray();
                return ordered[0] with { MergedSources = sources };
            })
            .ToArray();

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return groupedCandidates
                .OrderByDescending(GetLanguageRank)
                .ThenBy(GetAdaptationSignal)
                .ThenByDescending(HasDescriptiveEvidence)
                .ThenBy(GetCreditCount)
                .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => GetFirstAuthor(candidate), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return groupedCandidates
            .OrderByDescending(candidate => GetMatchKind(candidate, searchText))
            .ThenByDescending(candidate => GetTokenOverlapScore(candidate, searchText))
            .ThenByDescending(GetLanguageRank)
            .ThenBy(GetAdaptationSignal)
            .ThenByDescending(HasDescriptiveEvidence)
            .ThenBy(GetCreditCount)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => GetFirstAuthor(candidate), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Live evidence (searching "moby dick") showed author-count alone is too
    // weak a signal: a legitimate scholarly edition with an editor's
    // introduction ("Herman Melville, Nigel Cliff") and an actual comic-book
    // adaptation ("Bill Sienkiewicz, Herman Melville") both credit two people,
    // so credit count alone cannot tell them apart, and plenty of real
    // adaptations credit only one name too ("T. W. Robinson, Herman
    // Melville" -- two credits; "Will Eisner" alone -- one credit, still a
    // comic). What distinguishes them is what the source itself says. Markers
    // are the same vocabulary as DeterministicBookMatcher.DerivativeTitleMarkers
    // plus terms that show up in a *description* rather than a title --
    // "Retells in graphic novel format...", "...abridged, simplified and
    // condensed for young readers...", "Presented in comic book format." were
    // the literal descriptions on four of the nine candidates tied for first
    // place on this exact search. This is deliberately ranking-only: an
    // adaptation still appears, just not ahead of the edition that isn't one.
    // It must never feed identity matching -- a requester who explicitly
    // wants the graphic novel is not wrong to want it.
    private static readonly string[] AdaptationRankingMarkers =
    [
        "abridged", "abridgement", "retold", "retelling", "adapted", "adaptation",
        "graphic novel", "comic book", "simplified", "condensed for young readers",
        "young readers", "study guide", "companion to", "summary of",
        "cliffsnotes", "cliff notes", "sparknotes",
    ];

    private static int GetAdaptationSignal(BookCandidate candidate)
    {
        var haystack = Matching.DeterministicBookMatcher.NormalizeWords(
            $"{candidate.Title} {candidate.Description}");
        return AdaptationRankingMarkers.Any(marker =>
            haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
    }

    /// <summary>
    /// Whether the source gave any descriptive text at all. Several of the
    /// candidates tied on every signal above carry no description, no cover,
    /// and no marker -- bare metadata stubs that are indistinguishable from a
    /// well-catalogued edition by text alone. Preferring the one a source
    /// actually bothered to describe is a better bet than falling straight to
    /// alphabetical, though it is still only a bet: a stub can be the right
    /// book too.
    /// </summary>
    private static bool HasDescriptiveEvidence(BookCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Description);

    private static int GetCreditCount(BookCandidate candidate) => candidate.Authors.Count;

    // No per-user/global language preference exists yet, so this is a fixed
    // default rather than a setting. Unknown-language candidates are treated
    // as neutral (not demoted) since we can't confirm they're a mismatch.
    private const string DefaultPreferredLanguage = "en";

    private static int GetLanguageRank(BookCandidate candidate) =>
        candidate.Language is null ||
        string.Equals(candidate.Language, DefaultPreferredLanguage, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

    private static string GetMatchKey(BookCandidate candidate)
    {
        // Merge on the work itself, not the print edition: FL only ever acquires
        // ebooks/audiobooks, so a hardcover-vs-paperback ISBN difference between
        // providers is not a reason to show two rows for the same book -- an
        // exact ISBN match no longer gets a separate, higher-priority key, since
        // that let differing print-edition ISBNs (which are expected, not a sign
        // of a different work) keep matching candidates apart. Title/author are
        // normalized (diacritics, case, punctuation) so minor formatting
        // differences between providers don't defeat the match; this does not
        // reach differently-punctuated titles (e.g. "Moby Dick" vs. "Moby-Dick;
        // or, The Whale") since that needs fuzzier title matching than this
        // static grouping key can do. Language is kept as its own bucket so a
        // foreign-language edition never merges behind (and hides) an English one.
        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            return $"provider:{candidate.ProviderId}:{candidate.ExternalId}";
        }

        var firstAuthor = GetFirstAuthor(candidate);
        var normalizedTitle = NormalizeTitleForGrouping(candidate.Title, firstAuthor);
        var normalizedAuthor = NormalizeAuthorForGrouping(firstAuthor);
        var languageGroup = GetLanguageGroup(candidate.Language);
        return $"work:{normalizedTitle}:{normalizedAuthor}:{languageGroup}";
    }

    private static string NormalizeTitleForGrouping(string title, string? firstAuthor)
    {
        // Edition packaging is dropped first, so the three Open Library/Google
        // Books rows for one book -- "Threshing Day", "Threshing Day (Wing and
        // Claw Collection)", "Threshing Day: Return to the Empyrean world with
        // thirteen stories..." -- become one result instead of three choices a
        // requester has no way to decide between. Whichever row is most
        // complete represents the group and MergedSources keeps every
        // provider's record linkable.
        //
        // WorkTitleCore refuses to reduce a qualifier that changes the product,
        // so an omnibus ("Little Women: the Trilogy"), a combined edition, or
        // an adaptation still stands as its own result.
        var normalized = CatalogText.NormalizeForMatch(
            Matching.WorkTitleCore.Reduce(title, firstAuthor));
        foreach (var article in new[] { "the ", "an ", "a " })
        {
            if (normalized.StartsWith(article, StringComparison.Ordinal))
            {
                return normalized[article.Length..];
            }
        }

        return normalized;
    }

    private static string GetLanguageGroup(string? language) =>
        string.IsNullOrWhiteSpace(language) ||
        string.Equals(language, DefaultPreferredLanguage, StringComparison.OrdinalIgnoreCase)
            ? DefaultPreferredLanguage
            : language.Trim().ToLowerInvariant();

    private static string NormalizeAuthorForGrouping(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return string.Empty;
        }

        // Strip trailing notes some providers include (e.g. "Carr, Jack (Joint
        // pseudonym)") and reorder "Last, First" to "First Last" so it lines up
        // with the form other providers use for the same person.
        var openParenIndex = author.IndexOf('(');
        var withoutParenthetical = (openParenIndex < 0 ? author : author[..openParenIndex]).Trim();

        var commaIndex = withoutParenthetical.IndexOf(',');
        var reordered = commaIndex > 0 && commaIndex < withoutParenthetical.Length - 1
            ? $"{withoutParenthetical[(commaIndex + 1)..].Trim()} {withoutParenthetical[..commaIndex].Trim()}"
            : withoutParenthetical;

        return reordered.Length == 0 ? string.Empty : CatalogText.NormalizeForMatch(reordered);
    }

    private static int GetCompletenessScore(BookCandidate candidate) =>
        (string.IsNullOrWhiteSpace(candidate.Description) ? 0 : 2) +
        (string.IsNullOrWhiteSpace(candidate.CoverUrl) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(candidate.Publisher) ? 0 : 1) +
        (candidate.PageCount is null ? 0 : 1) +
        (candidate.Subjects.Count == 0 ? 0 : 1) +
        candidate.Authors.Count +
        candidate.Editions.Count +
        candidate.Series.Count;

    private static string? GetFirstAuthor(BookCandidate candidate) =>
        candidate.Authors.Count == 0 ? null : candidate.Authors[0];


    public static BookCandidateMatchKind GetMatchKind(BookCandidate candidate, string searchText)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var titleMatch = GetTextMatch(candidate.Title, searchText);
        var authorMatch = candidate.Authors
            .Select(author => GetTextMatch(author, searchText))
            .DefaultIfEmpty(TitleMatch.None)
            .Max();
        var match = titleMatch > authorMatch ? titleMatch : authorMatch;

        return match switch
        {
            TitleMatch.Exact => BookCandidateMatchKind.Exact,
            TitleMatch.StartsWithSearch => BookCandidateMatchKind.Close,
            TitleMatch.ContainsSearch => BookCandidateMatchKind.Contains,
            _ => BookCandidateMatchKind.Other
        };
    }

    // A pure substring/prefix/exact check gives no credit to a near-miss --
    // e.g. a typo'd search ("back luck and trouble") shares no substring with
    // its intended title ("Bad Luck and Trouble") at all, so it would
    // otherwise tie with completely unrelated results in the same MatchKind
    // tier and fall back to alphabetical order. Token overlap (what fraction
    // of the search's words appear, whole, in the title/author) catches this
    // without attempting real fuzzy/edit-distance matching.
    private static double GetTokenOverlapScore(BookCandidate candidate, string searchText)
    {
        var titleScore = GetTokenOverlapScore(candidate.Title, searchText);
        var authorScore = candidate.Authors
            .Select(author => GetTokenOverlapScore(author, searchText))
            .DefaultIfEmpty(0d)
            .Max();
        return Math.Max(titleScore, authorScore);
    }

    private static double GetTokenOverlapScore(string text, string searchText)
    {
        var searchTokens = NormalizeForSearch(searchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (searchTokens.Length == 0)
        {
            return 0d;
        }

        var textTokens = new HashSet<string>(
            NormalizeForSearch(text).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
        var matchedTokenCount = searchTokens.Count(textTokens.Contains);
        return (double)matchedTokenCount / searchTokens.Length;
    }

    private static TitleMatch GetTextMatch(string title, string searchText)
    {
        var normalizedTitle = NormalizeForSearch(title);
        var normalizedSearchText = NormalizeForSearch(searchText);
        if (normalizedTitle.Length == 0 || normalizedSearchText.Length == 0)
        {
            return TitleMatch.None;
        }

        var comparableTitle = TrimLeadingArticle(normalizedTitle);
        var comparableSearchText = TrimLeadingArticle(normalizedSearchText);
        if (string.Equals(comparableTitle, comparableSearchText, StringComparison.Ordinal))
        {
            return TitleMatch.Exact;
        }

        if (comparableTitle.StartsWith(
                string.Concat(comparableSearchText, " "),
                StringComparison.Ordinal))
        {
            return TitleMatch.StartsWithSearch;
        }

        return comparableTitle.Contains(comparableSearchText, StringComparison.Ordinal)
            ? TitleMatch.ContainsSearch
            : TitleMatch.None;
    }

    private static string NormalizeForSearch(string value)
    {
        var characters = value
            .Trim()
            .ToUpperInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(characters)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TrimLeadingArticle(string value)
    {
        foreach (var article in new[] { "A ", "AN ", "THE " })
        {
            if (value.StartsWith(article, StringComparison.Ordinal))
            {
                return value[article.Length..];
            }
        }

        return value;
    }

    private enum TitleMatch
    {
        None,
        ContainsSearch,
        StartsWithSearch,
        Exact
    }
}

public enum BookCandidateMatchKind
{
    Other,
    Contains,
    Close,
    Exact
}
