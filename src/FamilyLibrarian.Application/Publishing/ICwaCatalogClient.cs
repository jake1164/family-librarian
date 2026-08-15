namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Read-only OPDS catalog lookup, used to confirm a handed-off file actually
/// landed in the CWA/Calibre-Web library.
/// </summary>
/// <remarks>"Not found yet" is an expected, common outcome — CWA's ingest is
/// asynchronous — so this returns <c>null</c> rather than throwing.</remarks>
public interface ICwaCatalogClient
{
    Task<string?> FindBookIdAsync(string title, string? author, CancellationToken cancellationToken);
}
