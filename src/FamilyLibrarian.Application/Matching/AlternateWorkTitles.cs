namespace FamilyLibrarian.Application.Matching;

/// <summary>Which catalog edition titles may stand as alternate titles of a work.</summary>
public static class AlternateWorkTitles
{
    /// <summary>
    /// False for a "title" made only of an author's name. Metadata sources sometimes put the author in an edition's
    /// title field; used as an alternate title, it matched any release named "... by &lt;author&gt;", which is how a
    /// request for one book downloaded the same author's other book. A title that also carries other words is kept.
    /// </summary>
    /// <remarks>
    /// The <em>reduced</em> form is checked too, because that is what search and acceptance use: "Rebecca Yarros - Onyx
    /// Storm (The Empyrean Book 3) (2025)" reduces (subtitle after " - " removed) to "Rebecca Yarros", and on the lab that
    /// reduced title searched for and accepted the author's other book.
    /// </remarks>
    public static bool IsUsable(string title, IEnumerable<string?> authorNames)
    {
        ArgumentNullException.ThrowIfNull(authorNames);
        var authors = authorNames
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => DeterministicBookMatcher.AuthorTokens(author!))
            .Where(tokens => tokens.Count > 0)
            .ToArray();
        return IsUsableForm(title, authors) && IsUsableForm(WorkTitleCore.Reduce(title), authors);
    }

    private static bool IsUsableForm(string title, IReadOnlyList<HashSet<string>> authors)
    {
        var titleTokens = DeterministicBookMatcher.AuthorTokens(title);
        return titleTokens.Count > 0 && !authors.Any(titleTokens.IsSubsetOf);
    }
}
