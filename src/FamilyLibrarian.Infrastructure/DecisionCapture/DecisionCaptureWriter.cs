using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.DecisionCapture;

/// <summary>
/// Stores a captured decision in its own short-lived context, so a failure here can never poison or be blocked by the caller's
/// change tracker. The same snapshot seen again adds a call (and a sighting) instead of a duplicate row.
/// </summary>
public sealed class DecisionCaptureWriter(IServiceScopeFactory scopes) : IDecisionCaptureWriter
{
    public async Task WriteAsync(DecisionCaptureWrite write, CancellationToken cancellationToken)
    {
        try
        {
            await WriteOnceAsync(write, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent writer inserted the same snapshot between our lookup and our insert; the second pass attaches to it.
            await WriteOnceAsync(write, cancellationToken);
        }
    }

    private async Task WriteOnceAsync(DecisionCaptureWrite write, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var snapshot = write.Snapshot;
        var existing = await database.DecisionSnapshots.FirstOrDefaultAsync(candidate =>
            candidate.Role == snapshot.Role && candidate.SchemaVersion == snapshot.SchemaVersion &&
            candidate.SubjectReference == snapshot.SubjectReference && candidate.SnapshotDigest == snapshot.SnapshotDigest, cancellationToken);
        var call = write.Call;
        if (existing is null)
        {
            database.DecisionSnapshots.Add(snapshot);
            foreach (var link in write.Links)
            {
                database.CandidateLinks.Add(new CandidateLink(snapshot.Id, link.CandidateKey, link.ProviderId, link.ProviderResultId));
            }
        }
        else
        {
            existing.MarkSeen(call.CreatedAtUtc);
            call = Rebind(call, existing.Id);
            var known = (await database.CandidateLinks.Where(link => link.SnapshotId == existing.Id).Select(link => link.CandidateKey)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            foreach (var link in write.Links.Where(link => !known.Contains(link.CandidateKey)))
            {
                database.CandidateLinks.Add(new CandidateLink(existing.Id, link.CandidateKey, link.ProviderId, link.ProviderResultId));
            }
        }

        database.DecisionCalls.Add(call);
        await database.SaveChangesAsync(cancellationToken);
    }

    private static DecisionCall Rebind(DecisionCall call, Guid snapshotId) => new(
        snapshotId, call.ModelUsageRecordId, call.SemanticFindingId, call.SettingsId, call.Placement, call.ModelAlias, call.ReportedModel,
        call.Reasoning, call.RenderedMessagesHash, call.ParserVersion, call.Outcome, call.FailureReason, call.Answer, call.CreatedAtUtc);
}

/// <summary>Logs capture problems by role and exception type only. Reading interests are never written to a log.</summary>
public sealed partial class LoggingDecisionCaptureLog(ILogger<LoggingDecisionCaptureLog> logger) : IDecisionCaptureLog
{
    public void TooLarge(ModelRole role) => LogTooLarge(logger, role);

    public void Failed(ModelRole? role, string exceptionType) => LogFailed(logger, role?.ToString() ?? "unknown", exceptionType);

    [LoggerMessage(EventId = 4201, Level = LogLevel.Warning,
        Message = "A {Role} decision was larger than the saved-decision size guard and was not saved.")]
    private static partial void LogTooLarge(ILogger logger, ModelRole role);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning,
        Message = "Saving a {Role} decision failed ({ExceptionType}); the decision itself was not affected.")]
    private static partial void LogFailed(ILogger logger, string role, string exceptionType);
}
