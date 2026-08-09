namespace FamilyLibrarian.Contracts.Catalog;

public sealed record CatalogSearchResponse(IReadOnlyList<CatalogBookCandidateResponse> Results);

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
    IReadOnlyList<CatalogSeriesResponse> Series);

public sealed record CatalogEditionResponse(
    string Title,
    string? Isbn13,
    string Format,
    DateOnly? PublicationDate);

public sealed record CatalogSeriesResponse(
    string Name,
    string? PositionLabel,
    bool IsPrimary);
