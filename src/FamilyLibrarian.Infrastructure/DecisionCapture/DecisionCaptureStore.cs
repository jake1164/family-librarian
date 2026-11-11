using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.DecisionCapture;

/// <summary>
/// Administration storage for decision capture: counts and sizes, retention and purge. It never reads or returns snapshot JSON.
/// </summary>
public sealed class DecisionCaptureStore(AppDbContext database) : IDecisionCaptureStore
{
    private const int DeleteBatch = 500;

    public async Task<DecisionCaptureInsight> GetInsightAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var snapshots = database.DecisionSnapshots.AsNoTracking();
        var perRole = await snapshots.GroupBy(snapshot => snapshot.Role)
            .Select(group => new
            {
                Role = group.Key,
                Count = group.Count(),
                Oldest = group.Min(snapshot => snapshot.FirstSeenAtUtc),
                Newest = group.Max(snapshot => snapshot.LastSeenAtUtc),
                Bytes = group.Sum(snapshot => (long)snapshot.SnapshotJson.Length)
            }).ToListAsync(cancellationToken);
        var labeled = await snapshots.Where(snapshot => database.DecisionOutcomes.Any(outcome => outcome.SnapshotId == snapshot.Id))
            .GroupBy(snapshot => snapshot.Role).Select(group => new { Role = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var roles = DecisionCaptureRoles.Scored.Select(role =>
        {
            var row = perRole.FirstOrDefault(candidate => candidate.Role == role);
            var withOutcomes = labeled.FirstOrDefault(candidate => candidate.Role == role)?.Count ?? 0;
            return row is null
                ? new DecisionCaptureRoleInsight(role, 0, 0, 0, null, null, 0)
                : new DecisionCaptureRoleInsight(role, row.Count, withOutcomes, row.Count - withOutcomes, row.Oldest, row.Newest, row.Bytes);
        }).ToList();

        var sizesAvailable = true;
        async Task<long> SizeAsync(string qualifiedTable)
        {
            try
            {
                return await database.Database
                    .SqlQuery<long>($"SELECT pg_total_relation_size({qualifiedTable}::regclass) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Sizes are a convenience. If the database will not say, the counts are still shown and the card says sizes are unavailable.
                sizesAvailable = false;
                return 0;
            }
        }

        var tables = new List<DecisionCaptureTableInsight>
        {
            new("Decision snapshots", await snapshots.LongCountAsync(cancellationToken), await SizeAsync("tuning.decision_snapshots"),
                await snapshots.MinAsync(snapshot => (DateTimeOffset?)snapshot.FirstSeenAtUtc, cancellationToken)),
            new("Calls against snapshots", await database.DecisionCalls.LongCountAsync(cancellationToken), await SizeAsync("tuning.decision_calls"),
                await database.DecisionCalls.MinAsync(call => (DateTimeOffset?)call.CreatedAtUtc, cancellationToken)),
            new("Observed outcomes", await database.DecisionOutcomes.LongCountAsync(cancellationToken), await SizeAsync("tuning.decision_outcomes"),
                await database.DecisionOutcomes.MinAsync(outcome => (DateTimeOffset?)outcome.ObservedAtUtc, cancellationToken)),
            new("Candidate links", await database.CandidateLinks.LongCountAsync(cancellationToken), await SizeAsync("tuning.candidate_links"), null),
            new("Findings", await database.SemanticFindings.LongCountAsync(cancellationToken), await SizeAsync("evidence.semantic_findings"),
                await database.SemanticFindings.MinAsync(finding => (DateTimeOffset?)finding.CreatedAtUtc, cancellationToken)),
            new("Usage ledger", await database.ModelUsageRecords.LongCountAsync(cancellationToken), await SizeAsync("integrations.model_usage_ledger"),
                await database.ModelUsageRecords.MinAsync(record => (DateTimeOffset?)record.OccurredAtUtc, cancellationToken))
        };

        return new DecisionCaptureInsight(nowUtc, roles, tables, tables.Sum(table => table.ApproximateBytes), sizesAvailable);
    }

    public async Task<DecisionCaptureRetentionCounts> CountRetentionAsync(
        DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(limits, nowUtc, cancellationToken);
        return new DecisionCaptureRetentionCounts(plan.SnapshotIds.Count, plan.CallIds.Count, plan.FindingCount);
    }

    public async Task<DecisionCaptureRetentionCounts> RunRetentionAsync(
        DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(limits, nowUtc, cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        foreach (var batch in plan.SnapshotIds.Chunk(DeleteBatch))
        {
            var ids = batch;
            // The database cascades to each snapshot's calls, outcomes and candidate links.
            await database.DecisionSnapshots.Where(snapshot => ids.Contains(snapshot.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var batch in plan.CallIds.Chunk(DeleteBatch))
        {
            var ids = batch;
            await database.DecisionCalls.Where(call => ids.Contains(call.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        var findingCutoff = nowUtc.AddDays(-limits.FindingsDays);
        var findings = await database.SemanticFindings.Where(finding => finding.CreatedAtUtc < findingCutoff).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DecisionCaptureRetentionCounts(plan.SnapshotIds.Count, plan.CallIds.Count, findings);
    }

    public async Task<DecisionCapturePurgeResult> PurgeAsync(ModelRole? role, CancellationToken cancellationToken)
    {
        var snapshots = role is { } selected
            ? database.DecisionSnapshots.Where(snapshot => snapshot.Role == selected)
            : database.DecisionSnapshots;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var calls = await database.DecisionCalls.CountAsync(call => snapshots.Any(snapshot => snapshot.Id == call.SnapshotId), cancellationToken);
        var outcomes = await database.DecisionOutcomes.CountAsync(outcome => snapshots.Any(snapshot => snapshot.Id == outcome.SnapshotId), cancellationToken);
        var links = await database.CandidateLinks.CountAsync(link => snapshots.Any(snapshot => snapshot.Id == link.SnapshotId), cancellationToken);
        var removed = await snapshots.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DecisionCapturePurgeResult(removed, calls, outcomes, links);
    }

    private sealed record Plan(IReadOnlyList<Guid> SnapshotIds, IReadOnlyList<Guid> CallIds, int FindingCount);

    private async Task<Plan> PlanAsync(DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var snapshots = await database.DecisionSnapshots.AsNoTracking()
            .Select(snapshot => new { snapshot.Id, snapshot.Role, snapshot.LastSeenAtUtc }).ToListAsync(cancellationToken);
        var outcomes = (await database.DecisionOutcomes.AsNoTracking()
            .Select(outcome => new { outcome.SnapshotId, outcome.Kind, outcome.Strength }).ToListAsync(cancellationToken))
            .ToLookup(outcome => outcome.SnapshotId);
        var rows = snapshots.Select(snapshot =>
        {
            var facts = outcomes[snapshot.Id].ToArray();
            var strong = facts.Where(fact => fact.Strength == DecisionOutcomeStrength.Strong).Select(fact => fact.Kind).Order().ToArray();
            return new SnapshotRow(
                snapshot.Id, snapshot.Role, snapshot.LastSeenAtUtc, facts.Length > 0, strong.Length > 0,
                facts.Any(fact => DecisionOutcomeKinds.IsPositive(fact.Kind)) && facts.Any(fact => !DecisionOutcomeKinds.IsPositive(fact.Kind)),
                strong.Length > 0 ? strong[0] : null);
        }).ToList();

        var doomed = new HashSet<Guid>();
        var unlabeledCutoff = nowUtc.AddDays(-limits.UnlabeledSnapshotDays);
        foreach (var row in rows.Where(row => !row.HasOutcome && row.LastSeenAtUtc < unlabeledCutoff))
        {
            doomed.Add(row.Id);
        }

        // A snapshot with a known outcome is never removed by the unlabeled-age rule, only by its own optional age or by the cap.
        if (limits.LabeledSnapshotDays is { } labeledDays)
        {
            var labeledCutoff = nowUtc.AddDays(-labeledDays);
            foreach (var row in rows.Where(row => row.HasOutcome && row.LastSeenAtUtc < labeledCutoff))
            {
                doomed.Add(row.Id);
            }
        }

        foreach (var group in rows.Where(row => !doomed.Contains(row.Id)).GroupBy(row => row.Role))
        {
            var excess = group.Count() - limits.SnapshotsPerRole;
            if (excess <= 0) continue;
            foreach (var id in CapEvictions(group.ToList(), excess))
            {
                doomed.Add(id);
            }
        }

        var crowded = await database.DecisionCalls.AsNoTracking()
            .GroupBy(call => call.SnapshotId).Where(group => group.Count() > limits.CallsPerSnapshot)
            .Select(group => group.Key).ToListAsync(cancellationToken);
        var callIds = new List<Guid>();
        foreach (var snapshotId in crowded.Where(id => !doomed.Contains(id)))
        {
            var id = snapshotId;
            callIds.AddRange(await database.DecisionCalls.AsNoTracking().Where(call => call.SnapshotId == id)
                .OrderByDescending(call => call.CreatedAtUtc).ThenByDescending(call => call.Id)
                .Skip(limits.CallsPerSnapshot).Select(call => call.Id).ToListAsync(cancellationToken));
        }

        var findingCutoff = nowUtc.AddDays(-limits.FindingsDays);
        var findings = await database.SemanticFindings.AsNoTracking().CountAsync(finding => finding.CreatedAtUtc < findingCutoff, cancellationToken);
        return new Plan([.. doomed], callIds, findings);
    }

    /// <summary>
    /// Which snapshots go when a role is over its cap. Order: no outcome, then weak outcomes only, then strongly labeled ones (balanced:
    /// the oldest of whichever outcome kind has the most, so a rare kind survives), and disputed ones last. Oldest first within a group.
    /// </summary>
    private static List<Guid> CapEvictions(List<SnapshotRow> candidates, int excess)
    {
        var evict = new List<Guid>(excess);
        List<SnapshotRow> Tier(Func<SnapshotRow, bool> pick) =>
            [.. candidates.Where(pick).OrderBy(row => row.LastSeenAtUtc).ThenBy(row => row.Id)];
        foreach (var tier in new[] { Tier(row => !row.HasOutcome), Tier(row => row.HasOutcome && !row.HasStrongOutcome) })
        {
            foreach (var row in tier.Take(excess - evict.Count)) evict.Add(row.Id);
        }

        var strong = Tier(row => row.HasStrongOutcome && !row.IsDisputed).GroupBy(row => row.PrimaryKind!.Value)
            .ToDictionary(group => group.Key, group => new Queue<SnapshotRow>(group));
        while (evict.Count < excess && strong.Count > 0)
        {
            // Take from the largest group; ties go to the group whose oldest snapshot is oldest.
            var largest = strong.OrderByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Value.Peek().LastSeenAtUtc).ThenBy(pair => pair.Key).First();
            evict.Add(largest.Value.Dequeue().Id);
            if (largest.Value.Count == 0) strong.Remove(largest.Key);
        }

        foreach (var row in Tier(row => row.IsDisputed).Take(excess - evict.Count)) evict.Add(row.Id);
        return evict;
    }

    private sealed record SnapshotRow(
        Guid Id, ModelRole Role, DateTimeOffset LastSeenAtUtc, bool HasOutcome, bool HasStrongOutcome, bool IsDisputed, DecisionOutcomeKind? PrimaryKind);
}
