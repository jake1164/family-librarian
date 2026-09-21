namespace FamilyLibrarian.Contracts.Catalog;

/// <summary>
/// Identifies a raw catalog search candidate for an availability check --
/// deliberately no ProviderId/ExternalId, since checking availability needs
/// only the book's identity, not where the search result came from.
/// </summary>
public sealed record CandidateAvailabilityRequest(
    string Title,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Isbn13s);

public sealed record CandidateAvailabilityResponse(
    IReadOnlyList<FulfillmentOptionResponse> Ebook,
    IReadOnlyList<FulfillmentOptionResponse> Audiobook);

/// <summary>Requester-safe availability fact; it intentionally has no provider identity.</summary>
public sealed record AvailabilityFactResponse(string OptionKind, string MediaType);

public sealed record CandidateAvailabilityRunStartedResponse(Guid RunId);

public sealed record CandidateAvailabilityRunResponse(
    bool IsComplete,
    IReadOnlyList<AvailabilityFactResponse> Availability);
