namespace FamilyLibrarian.Contracts.Catalog;

// Administrator-only catalog enrichment and inspection. Everything here is a sanitized projection: no entity objects, no raw provider
// payloads, no credentials, and no connection or SQL detail.

/// <summary>
/// The outcome of an administrator refresh of one Work. <see cref="Status"/> is <c>Completed</c> or <c>Partial</c>; a partial refresh
/// keeps every provider observation that committed and reports the rest.
/// </summary>
public sealed record WorkRefreshResponse(
    Guid WorkId,
    string Status,
    IReadOnlyList<WorkRefreshStepResponse> Steps,
    IReadOnlyList<TentativeProposalResponse> Proposals);

/// <param name="Phase"><c>Refresh</c> for a stored reference, <c>Discover</c> for a provider the Work had no reference to.</param>
/// <param name="Status">
/// Refreshed, Unchanged, Attached, Contradiction, Collision, NoProof, Tentative, NotFound, ProviderUnavailable, ProviderNotActive,
/// WorkRetired, Busy, Conflict, OutcomeUnknown, CatalogUnavailable, DeadlineReached or NoCandidate.
/// </param>
/// <param name="Reason">A typed reason where the status has one, for example a failed identity check.</param>
/// <param name="ContradictedIsbns">Persisted editions the fresh record now contradicts. Reported only: never moved or deleted.</param>
public sealed record WorkRefreshStepResponse(
    string ProviderId,
    string? ExternalId,
    string Phase,
    string Status,
    string? Reason,
    IReadOnlyList<string> ContradictedIsbns);

/// <summary>A plausible record from another source with no ISBN proof. Never persisted, never attached.</summary>
public sealed record TentativeProposalResponse(string ProviderId, string ExternalId, string Title, IReadOnlyList<string> Authors, string Reason);

/// <summary>
/// What is known about the evidence behind one saved Work: its source references, the bounded observation snapshots, and each fact's
/// selected value next to every claimed one. A reference with no snapshot (it predates evidence) is shown as exactly that, never
/// with invented provenance.
/// </summary>
public sealed record WorkSourcesAdminResponse(
    Guid WorkId,
    string Title,
    bool IsRetired,
    string? VersionClass,
    DateOnly? FirstPublicationDate,
    string? FirstPublicationPrecision,
    string? FirstPublicationBasis,
    IReadOnlyList<AdminSourceReferenceResponse> References,
    IReadOnlyList<AdminObservationResponse> Observations,
    bool ObservationsTruncated,
    IReadOnlyList<AdminFactResponse> Facts,
    bool ClaimsTruncated);

/// <param name="ProviderName">The installed provider's name, or null when it is no longer configured.</param>
/// <param name="ProviderState"><c>Configured</c> or <c>NotConfigured</c>.</param>
/// <param name="ObservedAtUtc">When the reference was first observed.</param>
/// <param name="LastSeenAtUtc">The latest time any snapshot of this record was seen again; null when no snapshot is retained.</param>
/// <param name="Provenance"><c>Snapshot</c> or <c>NoSnapshotRetained</c>.</param>
public sealed record AdminSourceReferenceResponse(
    string ProviderId,
    string? ProviderName,
    string ProviderState,
    string ExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    string Provenance);

/// <param name="Snapshot">The normalized snapshot (bounded to 8 KB), never a raw provider payload; null with <paramref name="SnapshotTruncated"/> when it was larger.</param>
/// <param name="Outcome">Observed, Contradiction, Collision or NoProof.</param>
public sealed record AdminObservationResponse(
    string ProviderId,
    string? ProviderName,
    string ProviderState,
    string ExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    int SchemaVersion,
    string ContentHash,
    string? Outcome,
    string? Reason,
    System.Text.Json.JsonElement? Snapshot,
    bool SnapshotTruncated);

/// <param name="Scope">A series id, an ISBN-13, an <c>unbound:</c> series label, or <c>work</c>.</param>
/// <param name="State">SingleSource, Agreed (sources agree; independence is not established) or Conflicted, for series positions.</param>
/// <param name="DateConflict">The best-ranked date claims name disjoint intervals.</param>
public sealed record AdminFactResponse(
    string Field,
    string Scope,
    bool Unbound,
    System.Text.Json.JsonElement? SelectedValue,
    string? State,
    bool DateConflict,
    IReadOnlyList<AdminClaimResponse> Claims);

public sealed record AdminClaimResponse(
    string ProviderId,
    string? ProviderName,
    string ProviderState,
    string SourceEntityId,
    System.Text.Json.JsonElement Value,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsCurrent,
    bool IsSelected,
    bool ContradictsSelected);

/// <summary>
/// One bounded page of one category of catalog issue. <see cref="NextCursor"/> is an opaque continuation, null at the end of the scan.
/// Nothing here is a verdict: possible duplicates and same-name groups are reported for an owner decision, never merged.
/// </summary>
public sealed record CatalogIssueReportResponse(
    string Category,
    IReadOnlyList<CatalogIssueResponse> Items,
    string? NextCursor);

public sealed record CatalogIssueResponse(string Kind, string Subject, string? Reason, IReadOnlyList<Guid> Ids, DateTimeOffset? ObservedAtUtc);
