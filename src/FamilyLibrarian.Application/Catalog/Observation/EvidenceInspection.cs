using System.Globalization;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Observation;

// Read-only administrator inspection of the evidence behind a saved Work. Nothing here writes, calls a provider, or exposes an entity,
// a raw provider payload or a credential: every type below is a sanitized, bounded projection.

/// <summary>The most each inspection response returns, so a response is bounded whatever a Work has accumulated.</summary>
public static class EvidenceInspectionLimits
{
    public const int MaximumObservations = 50;
    public const int MaximumClaims = 100;
    /// <summary>The most bytes one rendered snapshot may take in the response (8 KiB), measured as the client receives it: escapes included.</summary>
    public const int MaximumRenderedSnapshotBytes = 8 * 1024;
    public const int MaximumReferences = 100;
    public const int DefaultIssuePageSize = 50;
    public const int MaximumIssuePageSize = 100;
    /// <summary>The longest continuation token the host accepts: exactly the longest one it can issue for any legal catalog key (Kestrel's 8 KiB request line holds it).</summary>
    public const int MaximumCursorLength = EvidenceCursor.WorstCaseTokenLength;
}

public sealed record WorkEvidenceHeader(
    Guid WorkId,
    string Title,
    bool IsRetired,
    string? VersionClass,
    DateOnly? FirstPublicationDate,
    DatePrecision? FirstPublicationPrecision,
    DateBasis? FirstPublicationBasis);

public sealed record StoredReference(string ProviderId, string ExternalId, DateTimeOffset ObservedAtUtc);

public sealed record StoredObservation(
    string ProviderId,
    string ExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    int SchemaVersion,
    string ContentHash,
    string Snapshot);

public sealed record StoredClaim(
    MetadataClaimField Field,
    string ScopeKey,
    string ProviderId,
    string SourceEntityId,
    string ValueJson,
    string ValueHash,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsCurrent,
    bool IsSelected);

public enum EvidenceIssueCategory
{
    /// <summary>Saved Works sharing a title and first author. Tentative: never merged automatically.</summary>
    PossibleDuplicates,

    /// <summary>Authors that share a normalized name. Existing identities are never merged or remapped.</summary>
    SameNameAuthors,

    /// <summary>Series that share a normalized name.</summary>
    SameNameSeries,

    /// <summary>Author credits created because a name matched several authors. An unverified identity, not a person.</summary>
    ProvisionalCredits,

    /// <summary>Series memberships a source stated but that could not be bound to one series.</summary>
    UnboundSeriesClaims,

    /// <summary>Series positions on which sources disagree.</summary>
    ConflictedPositions,

    /// <summary>Observations that were refused as a contradiction, collision or missing proof.</summary>
    ContestedObservations
}

/// <param name="Subject">A human-readable label: a title, a name or a series label. Display text only.</param>
/// <param name="Reason">A typed reason where the category has one.</param>
/// <param name="Ids">Saved ids the item concerns (Works, Authors or Series), so an administrator can open them.</param>
public sealed record EvidenceIssue(string Kind, string Subject, string? Reason, IReadOnlyList<Guid> Ids, DateTimeOffset? ObservedAtUtc);

/// <param name="NextCursor">Opaque continuation; null when the scan reached its end. Truncation is honest: absence of items is only a statement about the window scanned.</param>
public sealed record EvidenceIssuePage(IReadOnlyList<EvidenceIssue> Items, string? NextCursor);

/// <summary>Reads stored evidence for inspection. No-tracking, bounded and read-only by construction.</summary>
public interface ICatalogEvidenceReader
{
    Task<WorkEvidenceHeader?> GetHeaderAsync(Guid workId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredReference>> ListReferencesAsync(Guid workId, int take, CancellationToken cancellationToken);

    /// <summary>Newest first. Asks for one more than <paramref name="take"/> callers intend to show, to learn whether more exist.</summary>
    Task<IReadOnlyList<StoredObservation>> ListObservationsAsync(Guid workId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredClaim>> ListClaimsAsync(Guid workId, int take, CancellationToken cancellationToken);

    /// <summary>The latest last-seen per source record, over every snapshot (not only the ones listed).</summary>
    Task<IReadOnlyDictionary<(string ProviderId, string ExternalId), DateTimeOffset>> LatestSeenAsync(Guid workId, CancellationToken cancellationToken);

    Task<EvidenceIssuePage> ListIssuesAsync(EvidenceIssueCategory category, string? cursor, int take, CancellationToken cancellationToken);
}

public sealed record InspectedReference(
    string ProviderId,
    string? ProviderName,
    bool ProviderConfigured,
    string ExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool HasSnapshot);

/// <param name="Snapshot">The normalized snapshot, or null when it exceeded the per-snapshot bound (see <paramref name="SnapshotTruncated"/>).</param>
public sealed record InspectedObservation(
    string ProviderId,
    string? ProviderName,
    string ExternalId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    int SchemaVersion,
    string ContentHash,
    string? Outcome,
    string? Reason,
    string? Snapshot,
    bool SnapshotTruncated);

public sealed record InspectedClaim(
    string ProviderId,
    string? ProviderName,
    string SourceEntityId,
    string ValueJson,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsCurrent,
    bool IsSelected,
    bool ContradictsSelected);

/// <param name="Scope">A series id, an ISBN-13, an <c>unbound:</c> label, or <c>work</c>.</param>
/// <param name="State">SingleSource, Agreed or Conflicted for a series position; null for other facts.</param>
/// <param name="DateConflict">The best-ranked date claims name disjoint intervals.</param>
public sealed record InspectedFact(
    string Field,
    string Scope,
    bool Unbound,
    string? SelectedValueJson,
    string? State,
    bool DateConflict,
    IReadOnlyList<InspectedClaim> Claims);

public sealed record WorkEvidenceView(
    WorkEvidenceHeader Work,
    IReadOnlyList<InspectedReference> References,
    IReadOnlyList<InspectedObservation> Observations,
    bool ObservationsTruncated,
    IReadOnlyList<InspectedFact> Facts,
    bool ClaimsTruncated);

/// <summary>Builds the administrator's view of a Work's evidence: provenance, selected versus claimed values, and what disagrees.</summary>
public sealed class CatalogEvidenceInspector(
    ICatalogEvidenceReader reader,
    IActiveMetadataProviderResolver providers)
{
    public async Task<WorkEvidenceView?> InspectWorkAsync(Guid workId, CancellationToken cancellationToken)
    {
        var header = await reader.GetHeaderAsync(workId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var references = await reader.ListReferencesAsync(workId, EvidenceInspectionLimits.MaximumReferences, cancellationToken);
        var observations = await reader.ListObservationsAsync(workId, EvidenceInspectionLimits.MaximumObservations + 1, cancellationToken);
        var claims = await reader.ListClaimsAsync(workId, EvidenceInspectionLimits.MaximumClaims + 1, cancellationToken);
        var seen = await reader.LatestSeenAsync(workId, cancellationToken);

        var observationsTruncated = observations.Count > EvidenceInspectionLimits.MaximumObservations;
        var claimsTruncated = claims.Count > EvidenceInspectionLimits.MaximumClaims;
        var shownClaims = claims.Take(EvidenceInspectionLimits.MaximumClaims).ToArray();

        return new WorkEvidenceView(
            header,
            references.Select(reference =>
            {
                var name = providers.RegisteredDisplayName(reference.ProviderId);
                var latest = seen.TryGetValue((reference.ProviderId, reference.ExternalId), out var at) ? at : (DateTimeOffset?)null;
                // A reference that predates evidence keeps its first-observation time and says plainly that no snapshot was retained.
                return new InspectedReference(
                    reference.ProviderId, name, name is not null, reference.ExternalId, reference.ObservedAtUtc, latest, latest is not null);
            }).ToArray(),
            observations.Take(EvidenceInspectionLimits.MaximumObservations).Select(ToInspected).ToArray(),
            observationsTruncated,
            BuildFacts(shownClaims),
            claimsTruncated);
    }

    public Task<EvidenceIssuePage> ListIssuesAsync(
        EvidenceIssueCategory category, string? cursor, int take, CancellationToken cancellationToken) =>
        reader.ListIssuesAsync(category, cursor, Math.Clamp(take, 1, EvidenceInspectionLimits.MaximumIssuePageSize), cancellationToken);

    private InspectedObservation ToInspected(StoredObservation observation)
    {
        string? outcome = null;
        string? reason = null;
        try
        {
            using var document = JsonDocument.Parse(observation.Snapshot);
            outcome = document.RootElement.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            reason = document.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            // An unreadable snapshot is shown as having no outcome rather than failing the whole inspection.
        }

        var oversize = EvidenceJson.RenderedByteCount(observation.Snapshot) > EvidenceInspectionLimits.MaximumRenderedSnapshotBytes;
        return new InspectedObservation(
            observation.ProviderId,
            providers.RegisteredDisplayName(observation.ProviderId),
            observation.ExternalId,
            observation.ObservedAtUtc,
            observation.LastSeenAtUtc,
            observation.SchemaVersion,
            observation.ContentHash,
            outcome,
            reason,
            oversize ? null : observation.Snapshot,
            oversize);
    }

    private InspectedFact[] BuildFacts(IReadOnlyList<StoredClaim> claims) =>
        claims.GroupBy(claim => (claim.Field, claim.ScopeKey))
            .OrderBy(group => group.Key.Field)
            .ThenBy(group => group.Key.ScopeKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var current = group.Where(claim => claim.IsCurrent).ToArray();
                var selected = group.FirstOrDefault(claim => claim.IsSelected);
                var (state, conflict) = Judge(group.Key.Field, current);
                return new InspectedFact(
                    group.Key.Field.ToString(),
                    group.Key.ScopeKey,
                    group.Key.ScopeKey.StartsWith("unbound:", StringComparison.Ordinal),
                    selected?.ValueJson,
                    state,
                    conflict,
                    group.OrderByDescending(claim => claim.IsSelected).ThenByDescending(claim => claim.IsCurrent)
                        .ThenBy(claim => claim.ProviderId, StringComparer.Ordinal).ThenBy(claim => claim.FirstSeenAtUtc)
                        .Select(claim => new InspectedClaim(
                            claim.ProviderId,
                            claim.ProviderId == ClaimLedger.LegacyProvider ? null : providers.RegisteredDisplayName(claim.ProviderId),
                            claim.SourceEntityId,
                            claim.ValueJson,
                            claim.FirstSeenAtUtc,
                            claim.LastSeenAtUtc,
                            claim.IsCurrent,
                            claim.IsSelected,
                            selected is not null && claim.ValueHash != selected.ValueHash))
                        .ToArray());
            })
            .ToArray();

    private static (string? State, bool DateConflict) Judge(MetadataClaimField field, StoredClaim[] current)
    {
        try
        {
            switch (field)
            {
                case MetadataClaimField.SeriesPosition:
                    var positions = current.Select(claim =>
                    {
                        using var document = JsonDocument.Parse(claim.ValueJson);
                        var root = document.RootElement;
                        var label = root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                        decimal? sort = root.TryGetProperty("sort", out var s) && s.ValueKind == JsonValueKind.String &&
                            decimal.TryParse(s.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                        return new PositionClaim(claim.ProviderId, claim.SourceEntityId, label, sort);
                    }).ToArray();
                    return (SeriesPositionSelection.Select(positions)?.State.ToString(), false);
                case MetadataClaimField.OriginalPublicationDate:
                    var dates = current.Select(claim =>
                    {
                        using var document = JsonDocument.Parse(claim.ValueJson);
                        var root = document.RootElement;
                        return new DateClaim(
                            claim.ProviderId, claim.SourceEntityId,
                            DateOnly.ParseExact(root.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                            Enum.Parse<DatePrecision>(root.GetProperty("precision").GetString()!),
                            Enum.Parse<DateBasis>(root.GetProperty("basis").GetString()!));
                    }).ToArray();
                    return (null, PublicationDateSelection.Select(dates)?.Conflict == true);
                default:
                    return (null, false);
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            // A claim value this reader cannot parse is shown, but no state is asserted about it.
            return (null, false);
        }
    }
}
