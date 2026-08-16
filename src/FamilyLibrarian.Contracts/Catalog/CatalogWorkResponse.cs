using FamilyLibrarian.Contracts.Policy;

namespace FamilyLibrarian.Contracts.Catalog;

public sealed record CatalogWorkResponse(
    Guid Id,
    string Title,
    IReadOnlyList<string> Authors,
    string? Description,
    string? CoverUrl,
    DateOnly? PublicationDate,
    IReadOnlyList<CatalogEditionResponse> Editions,
    IReadOnlyList<CatalogSeriesResponse> Series,
    IReadOnlyList<CatalogWorkSourceResponse> Sources);

public sealed record CatalogWorkSourceResponse(
    string ProviderId,
    string ExternalId,
    DateTimeOffset ObservedAtUtc);

public sealed record FulfillmentOptionResponse(
    string ProviderId,
    string ProviderResultId,
    string OptionKind,
    string AcquisitionMethod,
    string? ExternalActionUri);

public sealed record WorkFulfillmentOptionsResponse(
    IReadOnlyList<FulfillmentOptionResponse> Ebook,
    IReadOnlyList<FulfillmentOptionResponse> Audiobook,
    RecommendationResponse? EbookRecommendation = null,
    RecommendationResponse? AudiobookRecommendation = null);
