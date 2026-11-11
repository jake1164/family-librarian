using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.DecisionCapture;

/// <summary>
/// Attaches an observed fact to the saved decisions it is about, in its own short-lived context. Each (snapshot, candidate, kind) is
/// recorded once: a repeat, or a concurrent duplicate, adds nothing.
/// </summary>
public sealed class DecisionOutcomeWriter(IServiceScopeFactory scopes) : IDecisionOutcomeWriter
{
    private const string UnknownProvider = "unknown";

    public async Task<int> WriteAsync(DecisionOutcomeFact fact, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var targets = await ResolveAsync(database, fact.Target, cancellationToken);
        var attached = 0;
        foreach (var (snapshotId, candidateKey) in targets)
        {
            if (await database.DecisionOutcomes.AnyAsync(
                    outcome => outcome.SnapshotId == snapshotId && outcome.Kind == fact.Kind && outcome.CandidateKey == candidateKey, cancellationToken))
            {
                continue;
            }

            // An administrator could see the model's note only if a call to that decision actually produced one.
            var influenced = fact.AdministratorDecided &&
                await database.DecisionCalls.AnyAsync(call => call.SnapshotId == snapshotId && call.Outcome == ModelCallOutcome.Succeeded, cancellationToken);
            database.DecisionOutcomes.Add(new DecisionOutcome(snapshotId, candidateKey, fact.Kind, fact.Source, influenced, fact.Strength, observedAtUtc));
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                attached++;
            }
            catch (DbUpdateException)
            {
                // The same fact was recorded concurrently; the unique key keeps it once.
                database.ChangeTracker.Clear();
            }
        }

        return attached;
    }

    private static async Task<IReadOnlyList<(Guid SnapshotId, string? CandidateKey)>> ResolveAsync(
        AppDbContext database, DecisionOutcomeTarget target, CancellationToken cancellationToken)
    {
        if (target.MediaAssetId is { } assetId)
        {
            var ids = await database.DecisionSnapshots.AsNoTracking()
                .Where(snapshot => snapshot.MediaAssetId == assetId &&
                    (snapshot.Role == ModelRole.ArtifactValidationAssessment || snapshot.Role == ModelRole.ArtifactValidationDecision))
                .Select(snapshot => snapshot.Id).ToListAsync(cancellationToken);
            return [.. ids.Select(id => (id, (string?)null))];
        }

        if (target.RequestFormatId is { } formatId)
        {
            var subject = formatId.ToString("N");
            var latest = await database.DecisionSnapshots.AsNoTracking()
                .Where(snapshot => snapshot.Role == ModelRole.SearchGuidance && snapshot.SubjectReference == subject)
                .OrderByDescending(snapshot => snapshot.LastSeenAtUtc).Select(snapshot => (Guid?)snapshot.Id).FirstOrDefaultAsync(cancellationToken);
            return latest is { } id ? [(id, null)] : [];
        }

        if (target is { RequestId: { } requestId, ProviderId: { } providerId, ProviderResultId: { } resultId })
        {
            var rows = await (from link in database.CandidateLinks.AsNoTracking()
                              join snapshot in database.DecisionSnapshots.AsNoTracking() on link.SnapshotId equals snapshot.Id
                              where snapshot.RequestId == requestId && link.ProviderResultId == resultId &&
                                  (link.ProviderId == providerId || link.ProviderId == UnknownProvider) &&
                                  (snapshot.Role == ModelRole.CandidateTriage || snapshot.Role == ModelRole.CandidateAmbiguityAssessment)
                              select new { snapshot.Id, link.CandidateKey }).ToListAsync(cancellationToken);
            return [.. rows.Select(row => (row.Id, (string?)row.CandidateKey))];
        }

        return [];
    }
}

/// <summary>
/// Runs retention after a write, at most once per interval. Singleton: the throttle must be shared by every request. It uses its own
/// scope, never throws, and logs failures by type only.
/// </summary>
public sealed class DecisionCaptureMaintenance(
    IServiceScopeFactory scopes, IDecisionCaptureLog log, TimeProvider time) : IDecisionCaptureMaintenance
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private long lastRunTicks;

    public async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref lastRunTicks);
        if (last != 0 && now - last < Interval.Ticks) return;
        // Only the caller that wins the swap runs it; concurrent writers skip.
        if (Interlocked.CompareExchange(ref lastRunTicks, now, last) != last) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var settings = await scope.ServiceProvider.GetRequiredService<Application.ModelGateway.IModelGatewaySettingsStore>().FindAsync(cancellationToken);
            var limits = settings?.CaptureLimits ?? DecisionCaptureLimits.Default;
            await scope.ServiceProvider.GetRequiredService<IDecisionCaptureStore>().RunRetentionAsync(limits, time.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Failed(null, exception.GetType().Name);
        }
    }
}
