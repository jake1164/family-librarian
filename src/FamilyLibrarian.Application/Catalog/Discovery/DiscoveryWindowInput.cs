using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>One credit of a discovered book exactly as the source listed it; the native id is kept only where the source supplied one.</summary>
public sealed record WindowCredit(int Ordinal, string Label, string? NativeAuthorId, string? Role = null);

/// <summary>One series membership claim of a discovered book. A conflicting second claim for one series is its own membership.</summary>
public sealed record WindowMembership(
    string Label,
    string? NativeSeriesId,
    string? PositionLabel,
    decimal? PositionSort,
    string Relationship,
    string? CanonicalState,
    string? CanonicalTargetId);

/// <summary>A book a source lookup returned, reduced to what a temporary context may retain.</summary>
public sealed record WindowBook(
    SourceRef Source,
    string Title,
    DateOnly? PublicationDate,
    DatePrecision? Precision,
    DateBasis? Basis,
    IReadOnlyList<WindowCredit> Credits,
    IReadOnlyList<WindowMembership> Memberships,
    string? VersionKind,
    string? VersionLabel,
    string? Format);

/// <summary>An author or series a source lookup returned with no book to hang it on.</summary>
public sealed record WindowRef(
    ObservedSubjectKind Kind,
    string Label,
    string? NativeId,
    string Relationship,
    string? Disambiguation,
    int? WorkCount,
    bool? IsCompleted,
    int? PrimaryBooksCount,
    string? CanonicalState,
    string? CanonicalTargetId);

/// <summary>Everything one explicit lookup produced, ready for bounded admission as a page-window context.</summary>
public sealed record DiscoveryWindowInput(
    ObservedDiscovery Header,
    string ProviderName,
    IReadOnlyList<WindowBook> Books,
    IReadOnlyList<WindowRef> Refs);

/// <summary>
/// The outcome of admitting a window. <see cref="Retained"/> items from the front of the result were kept; when it is below
/// <see cref="Offered"/> the context could not hold the rest and the result must say so, never claim exhaustion.
/// </summary>
public sealed record DiscoveryWindowLease(Guid ContextId, DateTimeOffset ExpiresAtUtc, int Offered, int Retained);

/// <summary>A read-only copy of one window context.</summary>
public sealed record DiscoveryWindowView(
    Guid ContextId,
    ObservedDiscovery Header,
    IReadOnlyList<ObservedBookRecord> Records,
    IReadOnlyList<ObservedRef> Refs,
    DateTimeOffset ExpiresAtUtc,
    bool Truncated);

/// <summary>A subject looked up for an explicit lookup: what it names, the book record it hangs on and, for a window, the lookup that made it.</summary>
public sealed record SubjectLookup(
    Guid ContextId,
    ObservedSubject Subject,
    ObservedBookRecord? AnchorRecord,
    ObservedDiscovery? Window,
    DateTimeOffset ExpiresAtUtc,
    bool IsRoot);
