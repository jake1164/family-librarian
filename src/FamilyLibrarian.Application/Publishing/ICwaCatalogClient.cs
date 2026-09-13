using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Read-only OPDS catalog lookup, used to confirm a handed-off file actually
/// landed in the CWA/Calibre-Web library.
/// </summary>
/// <remarks>
/// "Not found yet" is an expected, common outcome — CWA's ingest is
/// asynchronous — so this returns <see cref="BookMatchDecision.NoMatch"/>
/// rather than throwing. An ambiguous result (more than one distinct catalog
/// entry matches) comes back as <see cref="BookMatchDecision.Ambiguous"/>
/// with the conflicting candidates attached — see
/// <c>docs/family-librarian-book-matching-design-findings.md</c> for why
/// title/author matching alone must not guess between them.
/// </remarks>
public interface ICwaCatalogClient
{
    /// <param name="isbn13Candidates">
    /// Known ISBN-13s for the Work being matched, if any. Tried first, as an
    /// OPDS search query, before falling back to title/author matching.
    /// </param>
    /// <param name="acceptedLanguage">
    /// Non-null only when the requester already explicitly accepted a
    /// non-English candidate for the specific format this verifies. See
    /// <see cref="Matching.IBookMatcher.ResolveUnique"/> for why this widens
    /// eligibility rather than being compared against a candidate's language.
    /// </param>
    Task<BookMatchResult> FindBookIdAsync(
        string title,
        string? author,
        IReadOnlyCollection<string> isbn13Candidates,
        CancellationToken cancellationToken,
        string? acceptedLanguage = null);
}
