using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

/// <summary>
/// Read-only, no-tracking access to stored catalog evidence for administrator inspection. Every query is bounded in the database
/// (a page size or a window), nothing is tracked, and nothing here can write.
/// </summary>
public sealed class CatalogEvidenceReader(AppDbContext database) : ICatalogEvidenceReader
{
    /// <summary>The most Works one duplicate-scan window reads, so a scan never loads the catalog.</summary>
    private const int DuplicateWindow = 2_000;

    private const int MaximumIdsPerItem = 10;

    public Task<WorkEvidenceHeader?> GetHeaderAsync(Guid workId, CancellationToken cancellationToken) =>
        database.Works.AsNoTracking()
            .Where(work => work.Id == workId)
            .Select(work => new WorkEvidenceHeader(
                work.Id, work.CanonicalTitle, work.IsRetired, work.VersionClass, work.FirstPublicationDate,
                work.FirstPublicationPrecision, work.FirstPublicationBasis))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<StoredReference>> ListReferencesAsync(Guid workId, int take, CancellationToken cancellationToken) =>
        await database.ExternalReferences.AsNoTracking()
            .Where(reference => reference.EntityType == ExternalReferenceEntityType.Work && reference.EntityId == workId)
            .OrderBy(reference => reference.ProviderId).ThenBy(reference => reference.ExternalId)
            .Take(take)
            .Select(reference => new StoredReference(reference.ProviderId, reference.ExternalId, reference.ObservedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StoredObservation>> ListObservationsAsync(Guid workId, int take, CancellationToken cancellationToken) =>
        await database.SourceObservations.AsNoTracking()
            .Where(observation => observation.WorkId == workId)
            .OrderByDescending(observation => observation.ObservedAtUtc).ThenBy(observation => observation.Id)
            .Take(take)
            .Select(observation => new StoredObservation(
                observation.ProviderId, observation.ExternalId, observation.ObservedAtUtc, observation.LastSeenAtUtc,
                observation.SchemaVersion, observation.ContentHash, observation.Snapshot))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StoredClaim>> ListClaimsAsync(Guid workId, int take, CancellationToken cancellationToken) =>
        await database.MetadataClaims.AsNoTracking()
            .Where(claim => claim.WorkId == workId)
            .OrderBy(claim => claim.Field).ThenBy(claim => claim.ScopeKey).ThenBy(claim => claim.ProviderId)
            .ThenBy(claim => claim.FirstSeenAtUtc).ThenBy(claim => claim.Id)
            .Take(take)
            .Select(claim => new StoredClaim(
                claim.Field, claim.ScopeKey, claim.ProviderId, claim.SourceEntityId, claim.ValueJson, claim.ValueHash,
                claim.FirstSeenAtUtc, claim.LastSeenAtUtc, claim.IsCurrent, claim.IsSelected))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<(string ProviderId, string ExternalId), DateTimeOffset>> LatestSeenAsync(
        Guid workId, CancellationToken cancellationToken)
    {
        var rows = await database.SourceObservations.AsNoTracking()
            .Where(observation => observation.WorkId == workId)
            .GroupBy(observation => new { observation.ProviderId, observation.ExternalId })
            .Select(group => new { group.Key.ProviderId, group.Key.ExternalId, Seen = group.Max(observation => observation.LastSeenAtUtc) })
            .Take(EvidenceInspectionLimits.MaximumReferences)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => (row.ProviderId, row.ExternalId), row => row.Seen);
    }

    public Task<EvidenceIssuePage> ListIssuesAsync(
        EvidenceIssueCategory category, string? cursor, int take, CancellationToken cancellationToken) =>
        category switch
        {
            EvidenceIssueCategory.PossibleDuplicates => PossibleDuplicatesAsync(cursor, take, cancellationToken),
            EvidenceIssueCategory.SameNameAuthors => SameNameAsync("authors", "SameNameAuthor", cursor, take, cancellationToken),
            EvidenceIssueCategory.SameNameSeries => SameNameAsync("series", "SameNameSeries", cursor, take, cancellationToken),
            EvidenceIssueCategory.ProvisionalCredits => ProvisionalCreditsAsync(cursor, take, cancellationToken),
            EvidenceIssueCategory.UnboundSeriesClaims => UnboundSeriesAsync(cursor, take, cancellationToken),
            EvidenceIssueCategory.ConflictedPositions => ConflictedPositionsAsync(cursor, take, cancellationToken),
            _ => ContestedObservationsAsync(cursor, take, cancellationToken)
        };

    // ---------------------------------------------------------------------------------------------------------------- duplicates

    private async Task<EvidenceIssuePage> PossibleDuplicatesAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        // The key a group is judged by is (title, first author), so it is also the key a page continues from: strictly after the
        // last group that was fully handled. A title alone would repeat or skip the other authors' groups of the same title.
        var after = EvidenceCursor.Decode(EvidenceIssueCategory.PossibleDuplicates, cursor);
        var hasCursor = after is not null;
        var afterTitle = after?[0] ?? string.Empty;
        var afterAuthor = after?[1] ?? string.Empty;

        // One bounded window of Works in key order, never the whole catalog.
        var rows = await database.Database.SqlQuery<WorkWindowRow>($"""
            SELECT w.id AS "Id", w.canonical_title AS "Title", w.normalized_title AS "NormalizedTitle",
                   COALESCE(a.normalized_name, '') AS "FirstAuthor"
            FROM catalog.works w
            LEFT JOIN catalog.work_authors wa ON wa.work_id = w.id AND wa.ordinal = 0
            LEFT JOIN catalog.authors a ON a.id = wa.author_id
            WHERE NOT w.is_retired
              AND (NOT {hasCursor} OR (w.normalized_title, COALESCE(a.normalized_name, '')) > ({afterTitle}, {afterAuthor}))
            ORDER BY w.normalized_title, COALESCE(a.normalized_name, ''), w.id
            LIMIT {DuplicateWindow}
            """).ToListAsync(cancellationToken);

        var windowFull = rows.Count == DuplicateWindow;
        var groups = rows
            .GroupBy(row => (row.NormalizedTitle, row.FirstAuthor))
            .Select(group => (group.Key, Rows: group.ToList()))
            .ToList();

        // The last group of a full window may continue past it. If what the window shows of it already has two Works it is a
        // duplicate group however large it turns out to be: it is reported once, now, and the next page starts after its key, so a
        // group larger than the window can neither loop nor be skipped. A cut group with one visible Work is judged from its start
        // by the next page instead.
        var cutIndex = windowFull ? groups.Count - 1 : -1;
        var resumeIndex = cutIndex >= 0 && groups[cutIndex].Rows.Count < 2 ? cutIndex - 1 : groups.Count - 1;
        var candidates = groups.Take(resumeIndex + 1).Where(group => group.Rows.Count > 1).ToList();

        var items = new List<EvidenceIssue>();
        string? resume = null;
        if (windowFull && resumeIndex >= 0)
        {
            resume = EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, groups[resumeIndex].Key.NormalizedTitle, groups[resumeIndex].Key.FirstAuthor);
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var group = candidates[index];
            if (items.Count == take)
            {
                // The page is full and more remains: continue strictly after the last group shown.
                var last = candidates[index - 1].Key;
                return new EvidenceIssuePage(items, EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, last.NormalizedTitle, last.FirstAuthor));
            }

            items.Add(new EvidenceIssue(
                "PossibleDuplicate", group.Rows[0].Title, "SameTitleAndFirstAuthor",
                group.Rows.Select(row => row.Id).Take(MaximumIdsPerItem).ToArray(), null));
        }

        return new EvidenceIssuePage(items, resume);
    }

    // ---------------------------------------------------------------------------------------------------------------- same-name groups

    private async Task<EvidenceIssuePage> SameNameAsync(
        string table, string kind, string? cursor, int take, CancellationToken cancellationToken)
    {
        var after = EvidenceCursor.Decode(
            table == "authors" ? EvidenceIssueCategory.SameNameAuthors : EvidenceIssueCategory.SameNameSeries, cursor)?[0] ?? string.Empty;
        // Two fixed statements; nothing caller-supplied is concatenated into either.
        var rows = table == "authors"
            ? await database.Database.SqlQuery<NameGroupRow>($"""
                SELECT normalized_name AS "NormalizedName", min(canonical_name) AS "Display",
                       (array_agg(id ORDER BY created_at_utc, id))[1:{MaximumIdsPerItem}] AS "Ids"
                FROM catalog.authors WHERE normalized_name > {after}
                GROUP BY normalized_name HAVING count(*) > 1
                ORDER BY normalized_name LIMIT {take + 1}
                """).ToListAsync(cancellationToken)
            : await database.Database.SqlQuery<NameGroupRow>($"""
                SELECT normalized_name AS "NormalizedName", min(name) AS "Display",
                       (array_agg(id ORDER BY created_at_utc, id))[1:{MaximumIdsPerItem}] AS "Ids"
                FROM catalog.series WHERE normalized_name > {after}
                GROUP BY normalized_name HAVING count(*) > 1
                ORDER BY normalized_name LIMIT {take + 1}
                """).ToListAsync(cancellationToken);

        var shown = rows.Take(take).ToList();
        return new EvidenceIssuePage(
            shown.Select(row => new EvidenceIssue(kind, row.Display, "SameNormalizedName", row.Ids, null)).ToArray(),
            rows.Count > take
                ? EvidenceCursor.Encode(
                    table == "authors" ? EvidenceIssueCategory.SameNameAuthors : EvidenceIssueCategory.SameNameSeries, shown[^1].NormalizedName)
                : null);
    }

    // ---------------------------------------------------------------------------------------------------------------- credits and claims

    private async Task<EvidenceIssuePage> ProvisionalCreditsAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        var key = EvidenceCursor.Decode(EvidenceIssueCategory.ProvisionalCredits, cursor);
        var afterWork = key is null ? Guid.Empty : Guid.ParseExact(key[0], "D");
        var afterAuthor = key is null ? Guid.Empty : Guid.ParseExact(key[1], "D");
        var rows = await database.Database.SqlQuery<CreditRow>($"""
            SELECT wa.work_id AS "WorkId", wa.author_id AS "AuthorId", a.canonical_name AS "Name", w.created_at_utc AS "At"
            FROM catalog.work_authors wa
            JOIN catalog.authors a ON a.id = wa.author_id
            JOIN catalog.works w ON w.id = wa.work_id
            WHERE wa.binding_basis = 'Provisional' AND (wa.work_id, wa.author_id) > ({afterWork}, {afterAuthor})
            ORDER BY wa.work_id, wa.author_id
            LIMIT {take + 1}
            """).ToListAsync(cancellationToken);

        var shown = rows.Take(take).ToList();
        return new EvidenceIssuePage(
            shown.Select(row => new EvidenceIssue("ProvisionalCredit", row.Name, "NameMatchedSeveralAuthors", [row.WorkId, row.AuthorId], row.At)).ToArray(),
            rows.Count > take
                ? EvidenceCursor.Encode(EvidenceIssueCategory.ProvisionalCredits, shown[^1].WorkId.ToString("D"), shown[^1].AuthorId.ToString("D"))
                : null);
    }

    private async Task<EvidenceIssuePage> UnboundSeriesAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        var after = DecodeId(EvidenceIssueCategory.UnboundSeriesClaims, cursor);
        var rows = await database.Database.SqlQuery<UnboundRow>($"""
            SELECT c.id AS "Id", c.work_id AS "WorkId", COALESCE(c.value_json ->> 'seriesLabel', c.scope_key) AS "Label",
                   c.last_seen_at AS "At"
            FROM catalog.metadata_claims c
            WHERE c.field = 'SeriesPosition' AND c.scope_key LIKE 'unbound:%' AND c.is_current AND c.id > {after}
            ORDER BY c.id
            LIMIT {take + 1}
            """).ToListAsync(cancellationToken);

        var shown = rows.Take(take).ToList();
        return new EvidenceIssuePage(
            shown.Select(row => new EvidenceIssue("UnboundSeriesClaim", row.Label, "SeveralSeriesShareTheName", [row.WorkId], row.At)).ToArray(),
            rows.Count > take ? EvidenceCursor.Encode(EvidenceIssueCategory.UnboundSeriesClaims, shown[^1].Id.ToString("D")) : null);
    }

    private async Task<EvidenceIssuePage> ConflictedPositionsAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        var after = DecodeId(EvidenceIssueCategory.ConflictedPositions, cursor);
        var rows = await database.Database.SqlQuery<ConflictRow>($"""
            SELECT e.id AS "Id", e.work_id AS "WorkId", e.series_id AS "SeriesId", s.name AS "SeriesName", w.canonical_title AS "Title"
            FROM catalog.series_entries e
            JOIN catalog.series s ON s.id = e.series_id
            JOIN catalog.works w ON w.id = e.work_id
            WHERE e.position_state = 'Conflicted' AND e.id > {after}
            ORDER BY e.id
            LIMIT {take + 1}
            """).ToListAsync(cancellationToken);

        var shown = rows.Take(take).ToList();
        return new EvidenceIssuePage(
            shown.Select(row => new EvidenceIssue("ConflictedPosition", $"{row.Title} in {row.SeriesName}", "SourcesDisagree", [row.WorkId, row.SeriesId], null)).ToArray(),
            rows.Count > take ? EvidenceCursor.Encode(EvidenceIssueCategory.ConflictedPositions, shown[^1].Id.ToString("D")) : null);
    }

    private async Task<EvidenceIssuePage> ContestedObservationsAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        var after = DecodeId(EvidenceIssueCategory.ContestedObservations, cursor);
        var rows = await database.Database.SqlQuery<ContestedRow>($"""
            SELECT o.id AS "Id", o.work_id AS "WorkId", o.provider_id AS "ProviderId", o.external_id AS "ExternalId",
                   o.snapshot ->> 'outcome' AS "Outcome", o.snapshot ->> 'reason' AS "Reason", o.last_seen_at AS "At"
            FROM catalog.source_observations o
            WHERE o.snapshot ->> 'outcome' IN ('Collision', 'NoProof', 'Contradiction') AND o.id > {after}
            ORDER BY o.id
            LIMIT {take + 1}
            """).ToListAsync(cancellationToken);

        var shown = rows.Take(take).ToList();
        return new EvidenceIssuePage(
            shown.Select(row => new EvidenceIssue(row.Outcome ?? "Contested", $"{row.ProviderId}/{row.ExternalId}", row.Reason, [row.WorkId], row.At)).ToArray(),
            rows.Count > take ? EvidenceCursor.Encode(EvidenceIssueCategory.ContestedObservations, shown[^1].Id.ToString("D")) : null);
    }

    // ---------------------------------------------------------------------------------------------------------------- cursors

    /// <summary>The id a single-id cursor carries (validated by <see cref="EvidenceCursor"/>), or the empty id before the first page.</summary>
    private static Guid DecodeId(EvidenceIssueCategory category, string? cursor) =>
        EvidenceCursor.Decode(category, cursor) is { } key ? Guid.ParseExact(key[0], "D") : Guid.Empty;

    // Unmapped projection types for the bounded SQL above (EF Core SqlQuery): every column has a matching property.
    private sealed record WorkWindowRow(Guid Id, string Title, string NormalizedTitle, string FirstAuthor);

    private sealed record NameGroupRow(string NormalizedName, string Display, Guid[] Ids);

    private sealed record CreditRow(Guid WorkId, Guid AuthorId, string Name, DateTimeOffset At);

    private sealed record UnboundRow(Guid Id, Guid WorkId, string Label, DateTimeOffset At);

    private sealed record ConflictRow(Guid Id, Guid WorkId, Guid SeriesId, string SeriesName, string Title);

    private sealed record ContestedRow(Guid Id, Guid WorkId, string ProviderId, string ExternalId, string? Outcome, string? Reason, DateTimeOffset At);
}
