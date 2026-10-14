namespace FamilyLibrarian.Application.Matching;

/// <summary>Which catalog edition titles may stand as alternate titles of a work.</summary>
public static class AlternateWorkTitles
{
    /// <summary>
    /// False for a "title" made only of an author's name. Metadata sources sometimes put the author in an edition's
    /// title field; used as an alternate title, it matched any release named "... by &lt;author&gt;", which is how a
    /// request for one book downloaded the same author's other book. A title that also carries other words is kept.
    /// </summary>
    public static bool IsUsable(string title, IEnumerable<string?> authorNames)
    {
        ArgumentNullException.ThrowIfNull(authorNames);
        var titleTokens = DeterministicBookMatcher.AuthorTokens(title);
        if (titleTokens.Count == 0)
        {
            return false;
        }

        return !authorNames
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => DeterministicBookMatcher.AuthorTokens(author!))
            .Any(authorTokens => authorTokens.Count > 0 && titleTokens.IsSubsetOf(authorTokens));
    }
}
