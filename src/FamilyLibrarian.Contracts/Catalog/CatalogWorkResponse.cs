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
    RecommendationResponse? AudiobookRecommendation = null,
    FormatReadinessResponse? EbookReadiness = null,
    FormatReadinessResponse? AudiobookReadiness = null);

/// <summary>
/// Whether a user may request this format right now — a null value means
/// readiness wasn't computed (e.g. an older client), not that it's unready.
/// </summary>
public sealed record FormatReadinessResponse(bool IsReady, string? Reason);

/// <summary>
/// Site-root links for the CWA and Audiobookshelf destinations, for a plain
/// "open the library" navigation link rather than a per-book deep link. A
/// null entry means that destination isn't enabled or has no URL configured
/// — this is safe for any signed-in user, unlike the admin-only settings
/// responses it's derived from, since it carries only a URL.
/// </summary>
public sealed record ExternalLibraryLinksResponse(string? CwaUrl, string? AudiobookshelfUrl);
