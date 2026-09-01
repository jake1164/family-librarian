namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Read-only OPDS catalog lookup, used to confirm a handed-off file actually
/// landed in the CWA/Calibre-Web library.
/// </summary>
/// <remarks>
/// "Not found yet" is an expected, common outcome — CWA's ingest is
/// asynchronous — so this returns <c>null</c> rather than throwing. An
/// ambiguous result (more than one distinct catalog entry matches) also
/// returns <c>null</c> rather than guessing: title/author matching alone
/// cannot tell a title collision from a wrong-book match, so callers must
/// treat "not found" and "ambiguous" the same way.
/// </remarks>
public interface ICwaCatalogClient
{
    /// <param name="isbn13Candidates">
    /// Known ISBN-13s for the Work being matched, if any. Tried first, as an
    /// OPDS search query, before falling back to title/author matching.
    /// </param>
    Task<string?> FindBookIdAsync(
        string title,
        string? author,
        IReadOnlyCollection<string> isbn13Candidates,
        CancellationToken cancellationToken);
}
