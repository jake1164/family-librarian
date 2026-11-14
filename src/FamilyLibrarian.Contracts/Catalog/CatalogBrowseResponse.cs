namespace FamilyLibrarian.Contracts.Catalog;

// Read-only author and series browsing. Saved pages are keyed by stable Family Librarian ids; observed previews are
// keyed by a temporary, owner-private context. None of these types carries provider credentials, raw payloads,
// exception text, request/follow state or entity objects.

public sealed record CatalogBrowseSourceResponse(string ProviderId, string? ProviderName, DateTimeOffset ObservedAtUtc);

/// <param name="AuthorId">Saved author id, set on saved pages only.</param>
/// <param name="SubjectId">Temporary reference within the context, set on observed previews only. Not an identity.</param>
public sealed record CatalogBrowseCreditResponse(Guid? AuthorId, Guid? SubjectId, string Name, string? Role);

/// <param name="Position">The numeric position where one is recorded, else null. <paramref name="PositionLabel"/> is the source text verbatim.</param>
public sealed record CatalogBrowseMembershipResponse(
    Guid? SeriesId,
    Guid? SubjectId,
    string Name,
    string? PositionLabel,
    decimal? Position,
    bool IsPrimary);

/// <summary>
/// A book in a browse list. A saved book opens by <see cref="WorkId"/>; an observed one by its provider reference.
/// <see cref="Formats"/> and the version kind/label repeat facts the server already knew; empty means unknown.
/// <see cref="AssociationsTruncated"/> warns that credits or memberships were omitted, so they are not a complete list.
/// </summary>
public sealed record CatalogBrowseBookResponse(
    Guid? WorkId,
    string? ProviderId,
    string? ExternalId,
    string Title,
    DateOnly? PublicationDate,
    IReadOnlyList<CatalogBrowseCreditResponse> Credits,
    IReadOnlyList<CatalogBrowseMembershipResponse> Memberships,
    IReadOnlyList<CatalogBrowseSourceResponse> Sources,
    IReadOnlyList<string>? Formats = null,
    string? VersionKind = null,
    string? VersionLabel = null,
    bool AssociationsTruncated = false)
{
    public IReadOnlyList<string> Formats { get; init; } = Formats ?? [];
}

public sealed record CatalogBrowseEntryResponse(
    CatalogBrowseBookResponse Book,
    string? PositionLabel,
    decimal? Position);

public sealed record CatalogBrowseGroupResponse(
    Guid? SeriesId,
    Guid? SubjectId,
    string Name,
    IReadOnlyList<CatalogBrowseEntryResponse> Entries);

public sealed record CatalogBrowseNameChoiceResponse(
    Guid Id,
    string Name,
    int KnownBookCount,
    IReadOnlyList<string> SampleTitles);

/// <summary>Saved authors or series whose name equals the observed label. Separate choices; never merged or auto-selected.</summary>
public sealed record CatalogBrowseNameChoicesResponse(
    IReadOnlyList<CatalogBrowseNameChoiceResponse> Choices,
    bool Truncated);

/// <summary>Known saved books by one author. <see cref="Groups"/> are built from this page only.</summary>
public sealed record CatalogAuthorBrowseResponse(
    Guid AuthorId,
    string Name,
    int Page,
    bool HasMore,
    IReadOnlyList<CatalogBrowseGroupResponse> Groups,
    IReadOnlyList<CatalogBrowseEntryResponse> NoKnownSeries,
    IReadOnlyList<string> Limitations);

/// <summary>Known saved members of one series, in recorded order with unknown positions last.</summary>
public sealed record CatalogSeriesBrowseResponse(
    Guid SeriesId,
    string Name,
    int Page,
    bool HasMore,
    IReadOnlyList<CatalogBrowseEntryResponse> Entries,
    IReadOnlyList<string> Limitations);

/// <summary>Source-scoped preview of one observed author credit. The label is an unverified name, not a person.</summary>
public sealed record CatalogObservedAuthorBrowseResponse(
    Guid ContextId,
    Guid SubjectId,
    string Label,
    string ProviderId,
    string ProviderName,
    string AnchorExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int Page,
    bool HasMore,
    IReadOnlyList<CatalogBrowseGroupResponse> Groups,
    IReadOnlyList<CatalogBrowseEntryResponse> NoKnownSeries,
    CatalogBrowseNameChoicesResponse NameChoices,
    IReadOnlyList<string> Limitations);

/// <summary>Source-scoped preview of one observed series label, keeping every recorded position.</summary>
public sealed record CatalogObservedSeriesBrowseResponse(
    Guid ContextId,
    Guid SubjectId,
    string Label,
    string ProviderId,
    string ProviderName,
    string AnchorExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int Page,
    bool HasMore,
    IReadOnlyList<CatalogBrowseEntryResponse> Entries,
    CatalogBrowseNameChoicesResponse NameChoices,
    IReadOnlyList<string> Limitations);

/// <summary>A compact cached book for opening another observed record when its provider cannot be reached.</summary>
public sealed record CatalogObservedBookResponse(
    Guid ContextId,
    CatalogBrowseBookResponse Book,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> Limitations);

/// <summary>One temporary author or series reference issued with a candidate: where it appeared and its exact observed label.</summary>
public sealed record CatalogNavigationReferenceResponse(int Ordinal, string Label, Guid SubjectId);

/// <summary>
/// Optional navigation attached to a candidate. <see cref="ContextId"/> is null (with a limitation code) when the
/// temporary cache could not admit the context; that never makes the search or detail response fail.
/// </summary>
public sealed record CatalogBookNavigationResponse(
    Guid? ContextId,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<CatalogNavigationReferenceResponse> Authors,
    IReadOnlyList<CatalogNavigationReferenceResponse> Series,
    IReadOnlyList<string> Limitations);
