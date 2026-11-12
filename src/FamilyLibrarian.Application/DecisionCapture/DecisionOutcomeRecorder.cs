using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.DecisionCapture;

namespace FamilyLibrarian.Application.DecisionCapture;

/// <summary>Where a fact is attached: a file, a requested format's suggested searches, or one candidate of one request. Exactly one group is set.</summary>
public sealed record DecisionOutcomeTarget(
    Guid? MediaAssetId = null, Guid? RequestFormatId = null, Guid? RequestId = null, string? ProviderId = null, string? ProviderResultId = null)
{
    public static DecisionOutcomeTarget File(Guid mediaAssetId) => new(MediaAssetId: mediaAssetId);

    public static DecisionOutcomeTarget SuggestedSearches(Guid requestFormatId) => new(RequestFormatId: requestFormatId);

    public static DecisionOutcomeTarget Candidate(Guid requestId, string providerId, string providerResultId) =>
        new(RequestId: requestId, ProviderId: providerId, ProviderResultId: providerResultId);
}

/// <summary>
/// An observed fact about something a model looked at. Facts only, never a label: S5 turns them into expected answers. Silence is
/// never a fact, so callers report only what actually happened.
/// </summary>
/// <param name="AdministratorDecided">True when an administrator made the decision and could see the model's note first, so S5 can say the label favors that model.</param>
public sealed record DecisionOutcomeFact(
    DecisionOutcomeKind Kind, string Source, DecisionOutcomeStrength Strength, DecisionOutcomeTarget Target, bool AdministratorDecided = false);

/// <summary>
/// Called by trusted workflow code after something really happened. It must never throw and never change what the caller does: a
/// failure is swallowed and logged without content.
/// </summary>
public interface IDecisionOutcomeRecorder
{
    Task RecordAsync(DecisionOutcomeFact fact, CancellationToken cancellationToken);
}

/// <summary>Storage for facts. Finds the snapshots a fact belongs to; attaches it once per snapshot, candidate and kind (idempotent).</summary>
public interface IDecisionOutcomeWriter
{
    /// <returns>How many snapshots the fact was newly attached to (zero when nothing was saved for that subject, or it was already recorded).</returns>
    Task<int> WriteAsync(DecisionOutcomeFact fact, DateTimeOffset observedAtUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Runs retention (expiry, caps, call trimming, finding age) after a write, at most once per interval, so storage stays capped without
/// a background service. Never throws.
/// </summary>
public interface IDecisionCaptureMaintenance
{
    Task RunIfDueAsync(CancellationToken cancellationToken);
}

public sealed class DecisionOutcomeRecorder(
    IModelGatewaySettingsStore settings, IDecisionOutcomeWriter writer, Abstractions.IClock clock, IDecisionCaptureLog log,
    IDecisionCaptureMaintenance? maintenance = null) : IDecisionOutcomeRecorder
{
    public async Task RecordAsync(DecisionOutcomeFact fact, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fact);
            // Facts attach to saved decisions, so with saving off there is nothing new to attach them to.
            if (!DecisionCaptureService.ToView(await settings.FindAsync(cancellationToken)).DecisionCaptureEnabled) return;
            if (await writer.WriteAsync(fact, clock.UtcNow, cancellationToken) > 0 && maintenance is not null)
            {
                await maintenance.RunIfDueAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Failed(null, exception.GetType().Name);
        }
    }
}

public static class DecisionOutcomeReporting
{
    /// <summary>Reports a fact when a recorder is wired. Cheap when none is; never throws (the recorder swallows its own failures).</summary>
    public static Task ReportAsync(this IDecisionOutcomeRecorder? recorder, DecisionOutcomeKind kind, string source,
        DecisionOutcomeStrength strength, DecisionOutcomeTarget target, CancellationToken cancellationToken, bool administratorDecided = false) =>
        recorder?.RecordAsync(new DecisionOutcomeFact(kind, source, strength, target, administratorDecided), cancellationToken) ?? Task.CompletedTask;
}
