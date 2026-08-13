using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Client.Catalog;

/// <summary>
/// Remembers the last search so returning from a book's details page shows the
/// results the user already had, instead of an empty form they have to redo.
/// </summary>
public sealed class CatalogSearchState
{
    public string SearchText { get; set; } = string.Empty;
    public IReadOnlyList<CatalogBookCandidateResponse> Results { get; set; } = [];
    public IReadOnlyList<string> UnavailableProviders { get; set; } = [];
    public bool HasSearched { get; set; }
}
