using FamilyLibrarian.Contracts.Catalog;
using Microsoft.AspNetCore.Components.Authorization;

namespace FamilyLibrarian.Web.Client.Catalog;

/// <summary>
/// Remembers the last search so returning from a book's details page shows the
/// results the user already had, instead of an empty form they have to redo.
/// </summary>
public sealed class CatalogSearchState : IDisposable
{
    private readonly AuthenticationStateProvider? authentication;

    public CatalogSearchState()
    {
    }

    /// <summary>
    /// Results carry temporary author/series navigation references that are private to the signed-in user. When the
    /// session signs in or out, everything remembered here is dropped so another user can never be offered, or reuse,
    /// the previous user's references. Nothing here is written to browser storage.
    /// </summary>
    public CatalogSearchState(AuthenticationStateProvider authentication)
    {
        this.authentication = authentication;
        authentication.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> _) => Clear();

    /// <summary>Forgets the last search, its results (and their navigation references) and cached availability.</summary>
    public void Clear()
    {
        SearchText = string.Empty;
        Results = [];
        UnavailableProviders = [];
        HasSearched = false;
        CurrentPage = 1;
        HasMore = false;
        CompletedRunId = null;
        AvailabilityByResultKey.Clear();
    }

    public void Dispose()
    {
        if (authentication is not null) authentication.AuthenticationStateChanged -= OnAuthenticationStateChanged;
    }

    public string SearchText { get; set; } = string.Empty;
    public IReadOnlyList<CatalogBookCandidateResponse> Results { get; set; } = [];
    public IReadOnlyList<string> UnavailableProviders { get; set; } = [];
    public bool HasSearched { get; set; }
    public int CurrentPage { get; set; } = 1;
    public bool HasMore { get; set; }
    public Guid? CompletedRunId { get; set; }

    /// <summary>
    /// Availability is a point-in-time enrichment result. A fresh catalog
    /// query must not reuse an earlier empty (or positive) answer for the
    /// same provider result: a provider may have been enabled, recovered, or
    /// completed its own index work since that earlier lookup.
    /// </summary>
    public void BeginNewSearch()
    {
        AvailabilityByResultKey.Clear();
        CompletedRunId = null;
    }

    /// <summary>
    /// Availability badges already resolved for a result, keyed by
    /// <c>"{ProviderId}:{ExternalId}"</c> -- carried here (not just in
    /// <c>Search.razor</c>'s own state) so they survive navigating to a
    /// result's detail page and back instead of re-checking every source
    /// again.
    /// </summary>
    public Dictionary<string, CandidateAvailabilityRunResponse> AvailabilityByResultKey { get; } = [];

    public static CatalogBookCandidateResponse PreserveGroupedEditions(
        CatalogBookCandidateResponse fresh, CatalogBookCandidateResponse? cached) =>
        cached is null || fresh.VersionKind != cached.VersionKind ? fresh : fresh with
        {
            Sources = fresh.Sources.Count > 0 ? fresh.Sources : cached.Sources,
            Editions = fresh.Editions.Concat(cached.Editions).Distinct().ToArray()
        };
}
