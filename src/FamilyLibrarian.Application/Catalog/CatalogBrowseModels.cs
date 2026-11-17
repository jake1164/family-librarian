using System.Text.Json.Serialization;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>Stable, client-mappable codes for what a browse page does and does not know. Never raw provider text.</summary>
public static class CatalogBrowseLimitations
{
    /// <summary>Only Works already saved in the Family Librarian catalog are listed; this is not a full bibliography.</summary>
    public const string KnownLocalOnly = "known-local-only";

    /// <summary>Only records a source already returned to this user's search are listed, not everything the source holds.</summary>
    public const string SourceSample = "source-sample";

    /// <summary>Names matched by exact label are an association, not proof that two records share a person or series.</summary>
    public const string IdentityUnverified = "identity-unverified";

    /// <summary>Some observed records, labels or memberships were omitted to stay within the temporary-cache bounds.</summary>
    public const string Truncated = "truncated";

    /// <summary>A selected source failed during the search that produced this sample.</summary>
    public const string SourceFailed = "source-failed";

    /// <summary>Groups were built from the displayed page only; a group can continue on another page.</summary>
    public const string GroupingPageLocal = "grouping-page-local";

    /// <summary>Sources disagree about at least one position shown here; the number shown is the selected value, not the only claim.</summary>
    public const string PositionConflicted = "position-conflicted";

    /// <summary>The temporary navigation cache could not admit this context; the book itself is unaffected.</summary>
    public const string NavigationUnavailable = "navigation-unavailable";

    /// <summary>Some author or series labels no longer agree with the stored observation, so they carry no link.</summary>
    public const string NavigationPartial = "navigation-partial";

    /// <summary>This page is the result of one explicit source lookup, not a search sample; it says only what that lookup observed.</summary>
    public const string SourceLookup = "source-lookup";
}

public enum CatalogBrowseStatus
{
    Ok,
    NotFound,
    InvalidPage,
    Unauthenticated
}

/// <summary>The outcome of a browse read. <see cref="Value"/> is set only when <see cref="Status"/> is <see cref="CatalogBrowseStatus.Ok"/>.</summary>
public sealed record CatalogBrowseResult<T>(CatalogBrowseStatus Status, T? Value = default) where T : class;

// ---- Temporary, source-scoped observations (held by the host's operational cache, never persisted) ----

public enum ObservedSubjectKind
{
    Author = 1,
    Series = 2,

    /// <summary>One observed book record, issued so an explicit source lookup can name it without a client-supplied id.</summary>
    Book = 3
}

/// <summary>
/// One observed author credit. <see cref="Ordinal"/> is its index in the source's own credit list (omitted credits leave
/// gaps). <see cref="SubjectId"/> is a context-local reference, not a person identity.
/// </summary>
public sealed record ObservedCredit(int Ordinal, string Label, Guid SubjectId)
{
    /// <summary>The provider's own author id for this credit when the source supplied one, kept exactly (never cut) and never inferred from the name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeAuthorId { get; init; }

    /// <summary>The contributor role the source stated for this credit (illustrator, translator, ...), kept exactly. Null means the source stated none; it never means author.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    /// <summary>The source stated a role that was too long to keep, so it was dropped whole. This is not "no role": the credit must never be read as authorship.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RoleNotRetained { get; init; }
}

/// <summary>One observed series membership with the source's own position text, exactly as received.</summary>
public sealed record ObservedMembership(
    int Ordinal,
    string Label,
    string? PositionLabel,
    decimal? PositionSort,
    bool IsPrimary,
    Guid SubjectId)
{
    /// <summary>The provider's own series id when the source supplied one. A label is never promoted to this.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeSeriesId { get; init; }

    /// <summary>The source sent no series name (Google sends only an id). The label is empty and the page shows a fallback; it is never a name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NameMissing { get; init; }

    /// <summary><c>native</c>, or <c>derived:&lt;scope&gt;</c>; null for a membership an ordinary search or detail observed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Relationship { get; init; }

    /// <summary>Not stated, stated-unverified, verified or invalid, as the shared canonical-series evaluation concluded. Read-only; never a merge.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CanonicalState { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CanonicalTargetId { get; init; }
}

/// <summary>
/// A compact, immutable summary of one provider record as it was returned to a search, or fetched for one detail
/// page. Carries no description, payload, credentials or acquisition data. <see cref="Formats"/> and the version
/// kind/label are the same existing facts a normal candidate response already reports; unknown stays unknown.
/// </summary>
public sealed record ObservedBookRecord(
    string ProviderId,
    string ProviderName,
    string ExternalId,
    string Title,
    DateOnly? PublicationDate,
    IReadOnlyList<ObservedCredit> Credits,
    IReadOnlyList<ObservedMembership> Memberships,
    DateTimeOffset ObservedAtUtc,
    bool CreditsTruncated,
    bool MembershipsTruncated,
    IReadOnlyList<string>? Formats = null,
    string? VersionKind = null,
    string? VersionLabel = null,
    bool FactsTruncated = false)
{
    public IReadOnlyList<string> Formats { get; init; } = Formats ?? [];

    /// <summary>Whether the provider id above is a Work or an Edition, where the adapter said. Null offers no source lookup.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalReferenceEntityType? SourceKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatePrecision? PublicationDatePrecision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateBasis? PublicationDateBasis { get; init; }

    /// <summary>The subject issued for this book, so an explicit lookup can name it. Null when no lookup is possible.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? BookSubjectId { get; init; }
}

/// <summary>
/// What a temporary subject reference was bound to when it was issued: one <see cref="Kind"/> of relationship, in one
/// provider's record identified by <see cref="AnchorExternalId"/>, at that relationship's original
/// <see cref="Ordinal"/>, with the full observed <see cref="Label"/>. Two records, or two ordinals in one record, that
/// happen to share a label therefore have distinct subjects.
/// </summary>
public sealed record ObservedSubject(
    Guid SubjectId,
    ObservedSubjectKind Kind,
    string ProviderId,
    string ProviderName,
    string Label,
    string AnchorExternalId,
    int Ordinal)
{
    /// <summary>What <see cref="NativeExternalId"/> identifies at the provider (Work, Edition, Author or Series). Null when the source gave no native id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalReferenceEntityType? NativeKind { get; init; }

    /// <summary>The provider's own id for the entity, only where the source supplied it. Server-issued: a client never names it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeExternalId { get; init; }
}

/// <summary>
/// An author or series a source lookup returned without a book to hang it on (an author-series list, an author's choices). Its
/// <see cref="SubjectId"/> is a context-local reference exactly like a book's credit or membership, and the native id is kept whole.
/// </summary>
public sealed record ObservedRef(
    Guid SubjectId,
    ObservedSubjectKind Kind,
    string Label,
    string? NativeId,
    bool NameMissing,
    string Relationship,
    string? Disambiguation = null,
    int? WorkCount = null,
    bool? IsCompleted = null,
    int? PrimaryBooksCount = null,
    string? CanonicalState = null,
    string? CanonicalTargetId = null);

/// <summary>
/// What one explicit source lookup was and what it found, kept with the window that holds its items. Everything here is a
/// stable code or a count; no provider text, URL, key or header ever enters it.
/// </summary>
public sealed record ObservedDiscovery(
    Guid JourneyId,
    string Operation,
    string ProviderId,
    string ProviderName,
    string Outcome,
    string Completion,
    IReadOnlyList<string> Notes,
    string CapabilityState,
    string? ScopeFilter,
    Guid ParentContextId,
    Guid ParentSubjectId,
    ObservedSubjectKind SubjectKind,
    string SubjectLabel,
    int Attempts,
    int Retries,
    int Pages,
    int ItemsOffered,
    int ItemsRetained,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>What the lookup's subject identified at the provider, so a continuation rebuilds exactly the subject the operation was begun with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalReferenceEntityType? SubjectNativeKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectNativeId { get; init; }

    /// <summary>The server-issued continuation for the next page, when the source has one and the journey allows it. Never client-supplied.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinuationToken { get; init; }
}

/// <summary>
/// Everything one temporary context retains. This is also the canonical representation its serialized byte budget is
/// measured against: the retained records, the subjects they reference, the provider-failure set and the truncation
/// flag are the complete stored payload.
/// </summary>
public sealed record BrowseContextSnapshot(
    IReadOnlyList<ObservedBookRecord> Records,
    IReadOnlyList<ObservedSubject> Subjects,
    IReadOnlyList<string> FailedProviders,
    bool Truncated)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ObservedRef>? Refs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservedDiscovery? Discovery { get; init; }
}

/// <summary>A consistent view of one temporary context for one subject: the subject plus its provider's records.</summary>
public sealed record ObservedSample(
    ObservedSubject Subject,
    IReadOnlyList<ObservedBookRecord> ProviderRecords,
    bool ProviderFailed,
    bool ContextTruncated,
    DateTimeOffset ExpiresAtUtc);

// ---- Browse pages ----

public sealed record BrowseBookSource(string ProviderId, string? ProviderName, DateTimeOffset ObservedAtUtc);

public sealed record BrowseBookCredit(Guid? AuthorId, Guid? SubjectId, string Name, string? Role);

public sealed record BrowseBookMembership(
    Guid? SeriesId,
    Guid? SubjectId,
    string Name,
    string? PositionLabel,
    decimal? Position,
    bool IsPrimary,
    string? PositionState = null);

/// <summary>
/// A book in a browse list. Exactly one of <see cref="WorkId"/> or the provider reference is the way to open it.
/// <see cref="Formats"/> and the version kind/label are display facts the server already knew; an empty/unknown value
/// means the fact is not known, not that it is absent. <see cref="AssociationsTruncated"/> says credits or memberships
/// were omitted, so a short (or empty) list is not evidence of a complete one.
/// </summary>
public sealed record BrowseBook(
    Guid? WorkId,
    string? ProviderId,
    string? ExternalId,
    string Title,
    DateOnly? PublicationDate,
    IReadOnlyList<BrowseBookCredit> Credits,
    IReadOnlyList<BrowseBookMembership> Memberships,
    IReadOnlyList<BrowseBookSource> Sources,
    IReadOnlyList<string>? Formats = null,
    string? VersionKind = null,
    string? VersionLabel = null,
    bool AssociationsTruncated = false)
{
    public IReadOnlyList<string> Formats { get; init; } = Formats ?? [];
}

/// <summary>A book listed under one series with that series' own position text and number, where known.</summary>
/// <param name="PositionState">SingleSource, Agreed or Conflicted for a saved position; null is a legacy or observed position nobody has attributed.</param>
public sealed record BrowseEntry(BrowseBook Book, string? PositionLabel, decimal? Position, string? PositionState = null);

public sealed record BrowseGroup(Guid? SeriesId, Guid? SubjectId, string Name, IReadOnlyList<BrowseEntry> Entries);

public sealed record BrowseNameChoices(IReadOnlyList<BrowseNameChoice> Choices, bool Truncated);

public sealed record BrowseAuthorPage(
    Guid AuthorId,
    string Name,
    int Page,
    bool HasMore,
    IReadOnlyList<BrowseGroup> Groups,
    IReadOnlyList<BrowseEntry> Ungrouped,
    IReadOnlyList<string> Limitations);

public sealed record BrowseSeriesPage(
    Guid SeriesId,
    string Name,
    int Page,
    bool HasMore,
    IReadOnlyList<BrowseEntry> Entries,
    IReadOnlyList<string> Limitations);

public sealed record ObservedAuthorPreview(
    ObservedSubject Subject,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int Page,
    bool HasMore,
    IReadOnlyList<BrowseGroup> Groups,
    IReadOnlyList<BrowseEntry> Ungrouped,
    BrowseNameChoices NameChoices,
    IReadOnlyList<string> Limitations);

public sealed record ObservedSeriesPreview(
    ObservedSubject Subject,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int Page,
    bool HasMore,
    IReadOnlyList<BrowseEntry> Entries,
    BrowseNameChoices NameChoices,
    IReadOnlyList<string> Limitations);
