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
    public int CurrentPage { get; set; } = 1;
    public bool HasMore { get; set; }

    /// <summary>
    /// Availability is a point-in-time enrichment result. A fresh catalog
    /// query must not reuse an earlier empty (or positive) answer for the
    /// same provider result: a provider may have been enabled, recovered, or
    /// completed its own index work since that earlier lookup.
    /// </summary>
    public void BeginNewSearch() => AvailabilityByResultKey.Clear();

    /// <summary>
    /// Availability badges already resolved for a result, keyed by
    /// <c>"{ProviderId}:{ExternalId}"</c> -- carried here (not just in
    /// <c>Search.razor</c>'s own state) so they survive navigating to a
    /// result's detail page and back instead of re-checking every source
    /// again.
    /// </summary>
    public Dictionary<string, CandidateAvailabilityRunResponse> AvailabilityByResultKey { get; } = [];
}
