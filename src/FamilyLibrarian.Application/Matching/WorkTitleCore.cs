using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// Reduces a catalog title to the part that identifies the <em>work</em>,
/// dropping packaging that identifies only an edition.
/// </summary>
/// <remarks>
/// A request names a work; a metadata provider names an edition. Open Library
/// and Google Books routinely hand back titles like
/// <c>Moby Dick (Illustrated Classics)</c>, <c>Moby Dick by Herman Melville</c>,
/// or <c>Threshing Day: Return to the Empyrean world with thirteen stories...</c>.
/// Comparing those literally against a source's plainly-named release
/// (<c>Moby Dick; Or, The Whale</c>) can never succeed, so the request never
/// matches anything -- including records of the right book sitting in the
/// index.
/// <para>
/// Per docs/family-librarian-book-matching-design-findings.md §5 this
/// classifies qualifiers rather than stripping everything after punctuation:
/// a qualifier carrying a derivative, combined-work, or multi-volume marker
/// (<c>Abridged</c>, <c>Box Set</c>, <c>Books 1-3</c>, <c>A / B</c>) changes
/// what the item <em>is</em> and is never dropped. Reduction is also never
/// allowed to empty the title.
/// </para>
/// <para>
/// This only ever <em>widens</em> what can be compared. It does not decide a
/// match on its own: <see cref="DeterministicBookMatcher"/> still requires
/// author agreement, and the reduced form is a weaker basis than an exact one
/// (see <see cref="BookMatchBasis"/>).
/// </para>
/// </remarks>
public static class WorkTitleCore
{
    private static readonly string[] SubtitleSeparators = [":", " - ", " – ", " — "];

    // "Books 1-3", "Volumes 2 - 4": a multi-volume qualifier is a different
    // product, not edition noise, even though it carries no word from
    // DeterministicBookMatcher's derivative-marker list.
    private static readonly Regex VolumeRangeQualifier = new(
        @"\b(books?|vols?|volumes?|parts?)\b[^\d]{0,4}\d+\s*[-–—]\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Qualifiers that make the item a different product, beyond the
    // derivative/combined-work markers the matcher already knows. Kept here
    // rather than added to DeterministicBookMatcher.DerivativeTitleMarkers:
    // those reject a candidate outright, while these only stop a reduction --
    // "Little Women: the Trilogy" must not collapse onto "Little Women", but
    // it is not itself evidence that a candidate is bad.
    //
    // "Annotated", "Illustrated" and "Large Print" are deliberately absent:
    // they are the same work in different packaging, which is exactly what
    // this is meant to reduce away.
    private static readonly string[] ProductChangingWords =
    [
        "trilogy", "complete edition", "complete works", "complete collection",
        "collected works", "adapted", "adaptation", "retold", "graphic novel",
    ];

    /// <summary>
    /// The work-identifying core of <paramref name="title"/>. Returns the
    /// trimmed original when nothing can be safely dropped.
    /// </summary>
    /// <param name="expectedAuthor">
    /// When supplied, a trailing <c>by &lt;author&gt;</c> is dropped only if
    /// that author is token-equivalent to this one. Without it the suffix is
    /// kept: <c>Death by Black Hole</c> must not become <c>Death</c>.
    /// </param>
    public static string Reduce(string title, string? expectedAuthor = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var current = title.Trim();

        // A title can carry more than one layer of packaging, e.g.
        // "Moby Dick (Illustrated Classics) [Annotated]". Each pass drops at
        // most one, and the loop is bounded so a pathological title cannot
        // spin here.
        for (var pass = 0; pass < 4; pass++)
        {
            var reduced =
                StripTrailingQualifier(current) ??
                StripAuthorSuffix(current, expectedAuthor) ??
                StripSubtitle(current);
            if (string.IsNullOrWhiteSpace(reduced) || reduced.Length == current.Length)
            {
                break;
            }

            current = reduced;
        }

        return current;
    }

    /// <summary>
    /// Whether <paramref name="title"/> actually carries packaging that
    /// <see cref="Reduce"/> removes -- used to report that a comparison
    /// succeeded only on the reduced form.
    /// </summary>
    public static bool HasReducibleQualifier(string title, string? expectedAuthor = null) =>
        !string.Equals(Reduce(title, expectedAuthor), title?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Drops a trailing parenthesized or bracketed edition qualifier --
    /// "(Illustrated Classics)", "[Annotated]" -- when its contents are
    /// edition noise rather than a different product.
    /// </summary>
    private static string? StripTrailingQualifier(string value)
    {
        if (value.Length < 3)
        {
            return null;
        }

        var closer = value[^1];
        var opener = closer switch { ')' => '(', ']' => '[', _ => '\0' };
        if (opener == '\0')
        {
            return null;
        }

        var open = value.LastIndexOf(opener);
        if (open <= 0)
        {
            return null;
        }

        var inner = value[(open + 1)..^1].Trim();
        var head = value[..open].Trim();
        if (inner.Length == 0 || head.Length == 0 || IsProductChangingQualifier(inner))
        {
            return null;
        }

        return head;
    }

    /// <summary>
    /// Drops a trailing <c>by &lt;author&gt;</c> only when that author agrees
    /// with the one already expected, so an ordinary title containing the word
    /// "by" is left intact.
    /// </summary>
    private static string? StripAuthorSuffix(string value, string? expectedAuthor)
    {
        if (string.IsNullOrWhiteSpace(expectedAuthor))
        {
            return null;
        }

        var marker = value.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return null;
        }

        var head = value[..marker].Trim();
        var tail = value[(marker + 4)..].Trim();
        if (head.Length == 0 || tail.Length == 0)
        {
            return null;
        }

        var expectedTokens = DeterministicBookMatcher.AuthorTokens(expectedAuthor);
        var tailTokens = DeterministicBookMatcher.AuthorTokens(tail);
        return expectedTokens.Count > 0 && expectedTokens.SetEquals(tailTokens) ? head : null;
    }

    /// <summary>
    /// Drops a marketing subtitle. A one-word core is only trusted when what
    /// it drops is a long subtitle rather than a short distinguishing part of
    /// the title, so "Dune: Messiah" survives and
    /// "Threshing Day: Return to the Empyrean world..." does not.
    /// </summary>
    private static string? StripSubtitle(string value)
    {
        var cut = SubtitleSeparators
            .Select(separator => value.IndexOf(separator, StringComparison.Ordinal))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (cut < 0)
        {
            return null;
        }

        var core = value[..cut].Trim();
        var remainder = value[cut..];
        if (core.Length == 0 || IsProductChangingQualifier(remainder))
        {
            return null;
        }

        var coreWords = core.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var remainderWords = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return coreWords >= 2 || remainderWords >= 4 ? core : null;
    }

    private static bool IsProductChangingQualifier(string value)
    {
        if (DeterministicBookMatcher.HasDerivativeOrCombinedWorkMarker(value) ||
            VolumeRangeQualifier.IsMatch(value))
        {
            return true;
        }

        var comparableWords = DeterministicBookMatcher.NormalizeWords(value);
        return ProductChangingWords.Any(word =>
            comparableWords.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
