using System.Text;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Reads identity evidence out of a provider's raw <c>release.name</c>
/// (protocol v2 §7) for the common case of a source that reports a release
/// name and nothing else — no <c>work</c> object, no structured title or
/// author. Without this, such a candidate carries an empty
/// <see cref="ExternalProviderWorkEvidence.Title"/>, which every matcher in
/// <see cref="IBookMatcher"/> rejects outright, so a correct result is
/// indistinguishable from a wrong one and no request can ever be fulfilled
/// from that provider.
/// </summary>
/// <remarks>
/// This is deliberately a <em>verifier</em>, not a parser. Family Librarian
/// already knows the title and author it asked for, so the question put to a
/// release name is "does this name assert the work I requested, with every
/// token accounted for?" — never "which substring here is the title?".
/// Guessing which part of <c>Ray.Bradbury-Fahrenheit.451</c> is the title is
/// unnecessary and unsafe; confirming that it contains exactly the expected
/// title, the expected author, and nothing else is neither.
/// <para>
/// The load-bearing rule is <see cref="ReleaseNameVerdict.UnexplainedTokens"/>:
/// after the matched title, the matched author, and allowlisted release noise
/// are removed, nothing may remain. A release name that merely <em>contains</em>
/// the requested title and author is not the requested work —
/// <c>Ray Bradbury - A Pleasure to Burn-Fahrenheit 451 Stories</c> contains
/// both and is a different book. Only a fully explained name is treated as an
/// assertion of identity.
/// </para>
/// <para>
/// A release name is also the only place some facts appear at all, so a
/// language or narrator word in it is <em>extracted and reported</em>, never
/// discarded as noise. Stripping "Spanish" out of
/// <c>…2012.Spanish.Retail.EPUB…</c> as though it were a release tag would
/// turn a foreign-language edition into an unattended English acquisition.
/// </para>
/// </remarks>
public static class ExternalReleaseNameEvidence
{
    /// <summary>
    /// Container/codec and file-type words. This is a <em>noise</em> list, not
    /// an acceptance list, so it deliberately includes formats Family
    /// Librarian will not acquire: the token still has to be recognized as a
    /// format rather than left unexplained. Whether a format may actually be
    /// fetched stays with <see cref="ExternalEbookFormatPolicy"/> and
    /// <c>AudiobookFormatPolicy</c>.
    /// </summary>
    private static readonly HashSet<string> FormatTokens = new(StringComparer.Ordinal)
    {
        "EPUB", "EPUBC", "MOBI", "AZW", "AZW1", "AZW3", "AZW4", "KFX", "KF8", "PRC", "KEPUB",
        "FB2", "FBZ", "LIT", "PDB", "PDF", "RTF", "TXT", "DOC", "DOCX", "DJVU", "CBZ", "CBR",
        "M4B", "M4A", "MP3", "AAC", "OGG", "OGA", "OPUS", "FLAC", "WMA", "ALAC",
        "ZIP", "RAR", "7Z", "ISO"
    };

    /// <summary>Scope/provenance tags that say nothing about which work this is.</summary>
    private static readonly HashSet<string> QualityTokens = new(StringComparer.Ordinal)
    {
        "RETAIL", "REPACK", "PROPER", "REMASTERED", "UNABRIDGED"
    };

    /// <summary>Words naming the medium rather than the work.</summary>
    private static readonly HashSet<string> MediaTokens = new(StringComparer.Ordinal)
    {
        "EBOOK", "EBOOKS", "AUDIOBOOK", "AUDIOBOOKS"
    };

    /// <summary>
    /// Edition qualifiers of the same work. Deliberately narrow: words like
    /// "illustrated", "graphic", "adapted" or "dramatized" announce a
    /// different product and must stay unexplained so the candidate is
    /// refused rather than quietly accepted.
    /// </summary>
    private static readonly HashSet<string> EditionQualifierTokens = new(StringComparer.Ordinal)
    {
        "EDITION", "ANNIVERSARY", "REVISED", "REPRINT"
    };

    private static readonly HashSet<string> ConnectorTokens = new(StringComparer.Ordinal) { "BY" };

    private static readonly HashSet<string> NarratorLeadTokens = new(StringComparer.Ordinal)
    {
        "READ", "NARRATED", "PERFORMED"
    };

    /// <summary>A scene-style trailing group tag: <c>…eBook-BitBook</c>, but never <c>Fahrenheit 451 - Ray Bradbury</c>.</summary>
    private static readonly Regex TrailingGroupTag = new(
        @"(?<=[^\s\-])-([A-Za-z0-9]{2,})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OrdinalToken = new(
        @"^\d{1,3}(ST|ND|RD|TH)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Evaluates <paramref name="releaseName"/> against the expected work.
    /// </summary>
    /// <param name="expectedTitles">
    /// The canonical title plus any persisted edition-title aliases (PROVIDER-6);
    /// the first one that produces an assertion wins.
    /// </param>
    public static ReleaseNameVerdict Evaluate(
        string? releaseName, IReadOnlyList<string> expectedTitles, string? expectedAuthor,
        IReadOnlyList<BookSeries>? expectedSeries = null)
    {
        ArgumentNullException.ThrowIfNull(expectedTitles);

        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return ReleaseNameVerdict.None;
        }

        // The same raw-text negative evidence ordinary title matching applies
        // (derivative markers, a '/' combined-work separator, a spaced
        // ampersand), reusing that rule rather than restating it here.
        if (DeterministicBookMatcher.HasDerivativeOrCombinedWorkMarker(releaseName))
        {
            return ReleaseNameVerdict.Rejected(
                "The release name names a derivative or combined work, not the single title requested.");
        }

        ReleaseNameVerdict? best = null;
        foreach (var expectedTitle in expectedTitles.Where(title => !string.IsNullOrWhiteSpace(title)))
        {
            var verdict = EvaluateOne(releaseName, expectedTitle, expectedAuthor, expectedSeries);
            if (verdict.IsStrictWorkAssertion)
            {
                return verdict;
            }

            // Keep the most informative near-miss so a caller can explain why
            // a candidate was not auto-acquired.
            best ??= verdict;
            if (verdict.AssertsExpectedTitle && !best.AssertsExpectedTitle)
            {
                best = verdict;
            }
        }

        return best ?? ReleaseNameVerdict.None;
    }

    private static ReleaseNameVerdict EvaluateOne(
        string releaseName, string expectedTitle, string? expectedAuthor,
        IReadOnlyList<BookSeries>? expectedSeries)
    {
        var withoutGroupTag = TrailingGroupTag.Replace(releaseName, string.Empty);
        var tokens = Tokenize(withoutGroupTag);
        if (tokens.Count == 0)
        {
            return ReleaseNameVerdict.None;
        }

        var consumed = new bool[tokens.Count];

        var assertsTitle = ConsumeRun(tokens, consumed, TitleRunVariants(expectedTitle));
        var titleConsumed = consumed.ToArray();
        var assertsAuthor = expectedAuthor is not null &&
            ConsumeRun(tokens, consumed, [WordTokens(expectedAuthor)]);
        var exactAuthorEvidence = string.Join(' ', tokens
            .Where((_, index) => consumed[index] && !titleConsumed[index]).Select(token => token.Original));

        ConsumeSeries(tokens, consumed, expectedSeries);

        var narrator = ConsumeNarrator(tokens, consumed);
        string? language = null;
        string? format = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            if (consumed[index])
            {
                continue;
            }

            var token = tokens[index];
            var text = token.Text;

            if (FormatTokens.Contains(text))
            {
                format ??= text.ToLowerInvariant();
                consumed[index] = true;
                continue;
            }

            if (QualityTokens.Contains(text) || MediaTokens.Contains(text) ||
                EditionQualifierTokens.Contains(text) || ConnectorTokens.Contains(text) ||
                OrdinalToken.IsMatch(text) || IsYear(text))
            {
                consumed[index] = true;
                continue;
            }

            // A token inside square brackets is a release-group/format-list
            // annotation by convention. Parenthesised tokens get no such pass:
            // "(60th Anniversary)" and "(read by Christopher Hurt)" are
            // explained by the rules above, while "(BBC)" must stay
            // unexplained so a radio dramatization is not mistaken for the book.
            if (token.WasBracketed)
            {
                consumed[index] = true;
                continue;
            }

            if (LanguageAcceptance.TryResolveLanguageName(text, out var resolvedLanguage))
            {
                language ??= resolvedLanguage;
                consumed[index] = true;
                continue;
            }

            // A possessive remnant left by "Ray Bradbury's Fahrenheit 451"
            // once the author run itself has been matched.
            if (text == "S" && index > 0 && consumed[index - 1])
            {
                consumed[index] = true;
            }
        }

        var remainingAuthor = string.Join(' ', tokens.Where((_, index) => !consumed[index]).Select(token => token.Original));
        var affinity = AuthorAffinity.Evaluate(expectedAuthor, assertsAuthor ? exactAuthorEvidence : remainingAuthor, structured: false);
        if (!assertsAuthor && affinity.Kind is not (AuthorAffinityKind.Unknown or AuthorAffinityKind.Conflict))
        {
            for (var index = 0; index < tokens.Count; index++)
                if (!consumed[index] && AuthorAffinity.IsSupportingToken(expectedAuthor, tokens[index].Text))
                    consumed[index] = true;
            assertsAuthor = affinity.SupportsAutomaticIdentity;
        }

        var unexplained = tokens
            .Where((_, index) => !consumed[index])
            .Select(token => token.Text)
            .ToArray();

        return new ReleaseNameVerdict(
            assertsTitle, assertsAuthor, language, narrator, format, unexplained, RejectionReason: null, AuthorAffinity: affinity);
    }

    /// <summary>
    /// Accounts for the requested work's own series name and number
    /// (<c>The.Empyrean.3.5-Threshing.Day</c>), which a release name routinely
    /// carries. The number is only explained when the series name itself is
    /// present, and only when it equals the expected position, so a release of a
    /// different volume keeps its number unexplained and stays unaccepted.
    /// </summary>
    private static void ConsumeSeries(
        List<ReleaseNameToken> tokens, bool[] consumed, IReadOnlyList<BookSeries>? expectedSeries)
    {
        foreach (var series in expectedSeries ?? [])
        {
            if (string.IsNullOrWhiteSpace(series.Name) ||
                !ConsumeRun(tokens, consumed, SeriesNameRunVariants(series.Name)))
            {
                continue;
            }

            var position = WordTokens(series.Position ?? string.Empty);
            if (position.Length > 0)
            {
                ConsumeRun(tokens, consumed, [position]);
            }
        }
    }

    /// <summary>
    /// Series name runs to look for: <see cref="TitleRunVariants"/>'s handling,
    /// plus the reverse direction. A catalog's series name is routinely
    /// recorded without its branding article (<c>Empyrean</c>) while a release
    /// still carries it (<c>The.Empyrean.3.5-Threshing.Day</c>); without a
    /// "The"-prefixed variant that leading token is left unexplained forever
    /// and an otherwise fully-matching release is never auto-acquired.
    /// </summary>
    private static List<string[]> SeriesNameRunVariants(string seriesName)
    {
        var variants = TitleRunVariants(seriesName);

        // Tried before the bare name: a release that does carry the article
        // should consume it rather than leaving it as an unexplained "THE"
        // token, which ConsumeRun's first-match-wins search would otherwise
        // never reach because the shorter, article-less variant matches first.
        if (variants[0] is not ["THE", ..])
        {
            variants.Insert(0, ["THE", .. variants[0]]);
        }

        return variants;
    }

    /// <summary>
    /// The expected title as word runs to look for: as written, and with the
    /// leading/trailing article handled the same way
    /// <see cref="DeterministicBookMatcher"/> handles it, since a release name
    /// routinely drops or moves it.
    /// </summary>
    private static List<string[]> TitleRunVariants(string expectedTitle)
    {
        var variants = new List<string[]> { WordTokens(expectedTitle) };
        var withoutArticle = WordTokens(DeterministicBookMatcher.RemoveArticleVariants(expectedTitle));
        if (withoutArticle.Length > 0 && !withoutArticle.SequenceEqual(variants[0]))
        {
            variants.Add(withoutArticle);
        }

        return variants;
    }

    /// <summary>
    /// Marks the first contiguous, not-yet-consumed occurrence of any of
    /// <paramref name="runs"/>. Contiguity is what makes this safe: the
    /// expected words must appear together, not merely somewhere in the name.
    /// </summary>
    private static bool ConsumeRun(List<ReleaseNameToken> tokens, bool[] consumed, IReadOnlyList<string[]> runs)
    {
        foreach (var run in runs.Where(run => run.Length > 0))
        {
            for (var start = 0; start + run.Length <= tokens.Count; start++)
            {
                var matches = true;
                for (var offset = 0; offset < run.Length; offset++)
                {
                    if (consumed[start + offset] || tokens[start + offset].Text != run[offset])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                for (var offset = 0; offset < run.Length; offset++)
                {
                    consumed[start + offset] = true;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Consumes a "read by &lt;name&gt;" / "narrated by &lt;name&gt;" phrase and
    /// returns the credited reader. This is the only place audiobook narration
    /// evidence exists for a source that reports nothing but a release name.
    /// </summary>
    private static string? ConsumeNarrator(List<ReleaseNameToken> tokens, bool[] consumed)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (consumed[index] || !NarratorLeadTokens.Contains(tokens[index].Text))
            {
                continue;
            }

            var cursor = index + 1;
            if (cursor < tokens.Count && !consumed[cursor] && ConnectorTokens.Contains(tokens[cursor].Text))
            {
                cursor++;
            }
            else
            {
                // "Read" without "by" is not a narration credit.
                continue;
            }

            var nameParts = new List<string>();
            while (cursor < tokens.Count && !consumed[cursor] && IsPlainWord(tokens[cursor].Text) &&
                   !FormatTokens.Contains(tokens[cursor].Text) && !QualityTokens.Contains(tokens[cursor].Text) &&
                   !MediaTokens.Contains(tokens[cursor].Text))
            {
                nameParts.Add(tokens[cursor].Original);
                cursor++;
            }

            if (nameParts.Count == 0)
            {
                continue;
            }

            for (var mark = index; mark < cursor; mark++)
            {
                consumed[mark] = true;
            }

            return string.Join(' ', nameParts);
        }

        return null;
    }

    private static bool IsPlainWord(string text) => text.Length > 0 && text.All(char.IsAsciiLetter);

    private static bool IsYear(string text) =>
        text.Length == 4 && text.All(char.IsAsciiDigit) &&
        int.TryParse(text, out var year) && year is >= 1400 and <= 2100;

    private static string[] WordTokens(string value) => Tokenize(value).Select(token => token.Text).ToArray();

    /// <summary>
    /// Splits on every non-alphanumeric run, which is what makes
    /// <c>Ray.Bradbury-Fahrenheit.451</c> and
    /// <c>Fahrenheit 451 - Ray Bradbury</c> the same token sequence. A
    /// possessive apostrophe therefore leaves a bare <c>S</c> token, which the
    /// classification pass absorbs once the author run before it has matched,
    /// so "Ray Bradbury's Fahrenheit 451" still reads as an assertion.
    /// Square-bracket depth is tracked because bracketed content is
    /// conventionally a release annotation.
    /// </summary>
    private static List<ReleaseNameToken> Tokenize(string value)
    {
        var tokens = new List<ReleaseNameToken>();
        var builder = new StringBuilder();
        var bracketDepth = 0;
        var startedInBracket = false;

        void Flush()
        {
            if (builder.Length == 0)
            {
                return;
            }

            var original = builder.ToString();
            tokens.Add(new ReleaseNameToken(original.ToUpperInvariant(), original, startedInBracket));
            builder.Clear();
        }

        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (character == '[')
            {
                Flush();
                bracketDepth++;
                continue;
            }

            if (character == ']')
            {
                Flush();
                bracketDepth = Math.Max(0, bracketDepth - 1);
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (builder.Length == 0)
                {
                    startedInBracket = bracketDepth > 0;
                }

                builder.Append(character);
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;
    }

    private sealed record ReleaseNameToken(string Text, string Original, bool WasBracketed);
}

/// <summary>
/// What a release name asserts about the requested work. Reported facts only —
/// anything the name did not establish stays null/empty rather than being
/// filled in with a plausible value.
/// </summary>
public sealed record ReleaseNameVerdict(
    bool AssertsExpectedTitle,
    bool AssertsExpectedAuthor,
    string? AssertedLanguage,
    string? AssertedNarrator,
    string? AssertedFormat,
    IReadOnlyList<string> UnexplainedTokens,
    string? RejectionReason,
    AuthorAffinityResult? AuthorAffinity = null,
    bool StructuredTitlePlausible = false)
{
    public static readonly ReleaseNameVerdict None = new(false, false, null, null, null, [], null);

    public static ReleaseNameVerdict Rejected(string reason) =>
        new(false, false, null, null, null, [], reason);

    /// <summary>
    /// The only state that may stand in for structured title/author evidence:
    /// the name asserts both the expected title and the expected author, and
    /// every remaining token is accounted for. Title-only is reviewable
    /// evidence, never grounds for an unattended download — per the same
    /// same-title/missing-author rule the deterministic matcher applies.
    /// </summary>
    public bool IsStrictWorkAssertion =>
        AssertsExpectedTitle && AssertsExpectedAuthor &&
        UnexplainedTokens.Count == 0 && RejectionReason is null;
}
