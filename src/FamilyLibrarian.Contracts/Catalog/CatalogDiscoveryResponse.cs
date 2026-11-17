namespace FamilyLibrarian.Contracts.Catalog;

// Explicit source lookups ("ask this source about this book, series or author"). Every id here is a temporary,
// owner-private, server-issued reference; the client never names a native provider id, a URL, an owner or a budget. None of
// these types carries provider credentials, raw payloads, exception text, request/follow state or entity objects.

/// <summary>One lookup a subject offers. <see cref="Capability"/> is the integration's declared state (Supported, Unverified or Unsupported), not the outcome of any call.</summary>
public sealed record CatalogExpansionOfferResponse(
    string Kind,
    string ProviderId,
    string ProviderName,
    string Capability,
    string? ReasonCode,
    bool CanStart);

public sealed record CatalogExpansionOffersResponse(IReadOnlyList<CatalogExpansionOfferResponse> Offers);

/// <summary>Starts one lookup. <see cref="Kind"/> is one of BookSeries, SeriesMembers, ResolveAuthor, AuthorWorks, AuthorSeries.</summary>
public sealed record CatalogExpansionRequest(Guid ContextId, Guid SubjectId, string Kind);

/// <summary>Follows the next page of a lookup. Names the window only; the continuation itself never leaves the host.</summary>
public sealed record CatalogExpansionContinueRequest(Guid ContextId);

/// <summary>A refusal. <see cref="Code"/> is a stable code, never provider text.</summary>
public sealed record CatalogExpansionProblemResponse(string Code);

/// <param name="Role">The role the source stated (illustrator, translator, ...), or null when it stated none. Never an author unless the source says so.</param>
/// <param name="RoleNotRetained">The source stated a role that was too long to keep. The credit is then neither an author nor a known role.</param>
public sealed record CatalogDiscoveryCreditResponse(string Label, Guid SubjectId, bool HasSourceId, string? Role = null, bool RoleNotRetained = false);

/// <param name="Label">The series name exactly as the source gave it; empty when <paramref name="NameMissing"/> is true.</param>
/// <param name="Relationship"><c>native</c> or <c>derived:scope</c>.</param>
public sealed record CatalogDiscoveryMembershipResponse(
    Guid SubjectId,
    string Label,
    bool NameMissing,
    string? PositionLabel,
    decimal? Position,
    string? Relationship,
    string? CanonicalState,
    bool HasSourceId);

/// <param name="SourceKind">Work or Edition, as the source's own record.</param>
public sealed record CatalogDiscoveryBookResponse(
    Guid? BookSubjectId,
    string Title,
    string? SourceKind,
    DateOnly? PublicationDate,
    string? DatePrecision,
    string? DateBasis,
    IReadOnlyList<CatalogDiscoveryCreditResponse> Credits,
    IReadOnlyList<CatalogDiscoveryMembershipResponse> Memberships,
    string? VersionLabel,
    string? Format,
    bool AssociationsTruncated);

/// <summary>An author or series a lookup returned with no book to hang it on.</summary>
public sealed record CatalogDiscoveryRefResponse(
    Guid SubjectId,
    string Kind,
    string Label,
    bool NameMissing,
    bool HasSourceId,
    string Relationship,
    string? Disambiguation,
    int? WorkCount,
    bool? IsCompleted,
    int? PrimaryBooksCount,
    string? CanonicalState);

/// <summary>
/// What one explicit lookup observed, and nothing wider. <see cref="Outcome"/>, <see cref="Completion"/> and
/// <see cref="Notes"/> are stable codes; "Empty" means the checked scope held nothing, never that the source knows of nothing.
/// <see cref="ContextId"/> is null when the temporary cache could not hold even the header (the response still carries the outcome).
/// </summary>
public sealed record CatalogDiscoveryResponse(
    Guid? ContextId,
    Guid ParentContextId,
    Guid ParentSubjectId,
    string Operation,
    string ProviderId,
    string ProviderName,
    string SubjectKind,
    string SubjectLabel,
    string Outcome,
    string Completion,
    IReadOnlyList<string> Notes,
    string Capability,
    string? ScopeFilter,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    int Attempts,
    int Retries,
    int Pages,
    int ItemsOffered,
    int ItemsRetained,
    bool CanContinue,
    IReadOnlyList<CatalogDiscoveryBookResponse> Books,
    IReadOnlyList<CatalogDiscoveryRefResponse> Refs,
    IReadOnlyList<string> Limitations);
