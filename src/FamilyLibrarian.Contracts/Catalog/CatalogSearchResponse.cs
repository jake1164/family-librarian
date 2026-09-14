namespace FamilyLibrarian.Contracts.Catalog;

public sealed record CatalogSearchResponse(
    IReadOnlyList<CatalogBookCandidateResponse> Results,
    IReadOnlyList<CatalogProviderSearchStatusResponse> Providers,
    int Page = 1,
    bool HasMore = false);

public sealed record CatalogProviderSearchStatusResponse(
    string ProviderId,
    string ProviderName,
    bool Succeeded);

public sealed record CatalogBookCandidateResponse(
    string ProviderId,
    string ProviderName,
    string ExternalId,
    string Title,
    IReadOnlyList<string> Authors,
    string? Description,
    string? CoverUrl,
    DateOnly? PublicationDate,
    IReadOnlyList<CatalogEditionResponse> Editions,
    IReadOnlyList<CatalogSeriesResponse> Series,
    string? Publisher,
    int? PageCount,
    IReadOnlyList<string> Subjects,
    string? SourceUrl,
    string MatchKind = "Other",
    IReadOnlyList<CatalogCandidateSourceResponse>? Sources = null)
{
    // Populated when search grouped this candidate together with matching
    // records from other providers; empty for a single-provider detail fetch
    // that never went through that grouping.
    public IReadOnlyList<CatalogCandidateSourceResponse> Sources { get; init; } = Sources ?? [];
}

/// <summary>One provider's own record for a catalog candidate that search-result grouping merged into this one.</summary>
public sealed record CatalogCandidateSourceResponse(
    string ProviderId,
    string ProviderName,
    string ExternalId,
    string? SourceUrl);

public sealed record CatalogEditionResponse(
    string Title,
    string? Isbn13,
    string Format,
    DateOnly? PublicationDate);

/// <param name="Id">
/// The catalog <c>Series</c>' id, so it can be followed — only ever populated
/// once this series belongs to a resolved <c>Work</c> (see
/// <c>CatalogWorkResponse</c>); a raw, not-yet-resolved search candidate has
/// no persisted series to reference yet, so this is null there.
/// </param>
public sealed record CatalogSeriesResponse(
    string Name,
    string? PositionLabel,
    bool IsPrimary,
    Guid? Id = null);
