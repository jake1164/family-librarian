namespace FamilyLibrarian.Contracts.Catalog;

public sealed record CatalogSearchResponse(
    IReadOnlyList<CatalogBookCandidateResponse> Results,
    IReadOnlyList<CatalogProviderSearchStatusResponse> Providers);

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
    IReadOnlyList<string> Subjects);

public sealed record CatalogEditionResponse(
    string Title,
    string? Isbn13,
    string Format,
    DateOnly? PublicationDate);

public sealed record CatalogSeriesResponse(
    string Name,
    string? PositionLabel,
    bool IsPrimary);
