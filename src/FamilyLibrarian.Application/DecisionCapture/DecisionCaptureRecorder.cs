using System.Security.Cryptography;
using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.DecisionCapture;

/// <summary>
/// Turns a finished decision into a stored snapshot and call. The input is serialized through the tuner's allow-list contract
/// (so anything outside it is refused, never trimmed to fit), digested on its own terms, and re-rendered by the production
/// renderer: if that does not reproduce the recorded rendered-messages hash the snapshot is stored but marked stale, never
/// silently relabeled and never "fixed" by widening the allow-list.
/// </summary>
public sealed class DecisionCaptureRecorder(
    IModelGatewaySettingsStore settings, IDecisionCaptureWriter writer, IClock clock, IDecisionCaptureLog log,
    IDecisionCaptureMaintenance? maintenance = null) : IDecisionCaptureSink
{
    public const string StaleRenderMismatch = "render-mismatch";
    public const string StaleRenderFailed = "render-failed";
    public const string StaleExcerptsInvolved = "excerpts";
    public const int SchemaVersion = 1;

    public async Task CaptureAsync(CapturedDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(decision);
            // No ledger row means no model was called (a refusal, or a reused finding), so there is no decision to record.
            if (decision.Run.UsageRecordId is null || !DecisionCaptureRoles.Scored.Contains(decision.Role)) return;
            var current = await settings.FindAsync(cancellationToken);
            var view = DecisionCaptureService.ToView(current);
            if (!view.DecisionCaptureEnabled || (decision.Role == ModelRole.IntentInterpretation && !view.ChatCaptureEnabled)) return;

            var json = TuningInputJson.Write(decision.Role, decision.Input);
            if (Encoding.UTF8.GetByteCount(json) > DecisionCaptureLimits.MaxSnapshotBytes)
            {
                log.TooLarge(decision.Role);
                return;
            }

            var now = clock.UtcNow;
            var snapshot = new DecisionSnapshot(
                decision.Role, SchemaVersion, decision.SubjectReference, Digest(decision.Role, json), json,
                decision.RequestId, decision.UserId, decision.MediaAssetId, decision.ExcerptsInvolved, now);
            snapshot.MarkStale(StaleReasonFor(decision));
            var run = decision.Run;
            var call = new DecisionCall(
                snapshot.Id, run.UsageRecordId, run.FindingId, run.SettingsId, run.IsCloud ? ModelPlacement.Cloud : ModelPlacement.Local,
                run.ModelAlias, run.ReportedModel, run.Reasoning, run.RenderedMessagesHash ?? "none", run.ParserVersion,
                run.Outcome, run.FailureReason, run.Answer, now);
            await writer.WriteAsync(new DecisionCaptureWrite(snapshot, call, decision.Links ?? []), cancellationToken);
            if (maintenance is not null) await maintenance.RunIfDueAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Capture can never change a decision. Log the kind of failure only: never the input, which holds reading interests.
            log.Failed(decision?.Role, exception.GetType().Name);
        }
    }

    /// <summary>The stable identity of the safe input itself, not of the production reuse digest (which includes fields a snapshot must exclude).</summary>
    internal static string Digest(ModelRole role, string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{role}\n{SchemaVersion}\n{json}")));

    /// <summary>Null when the rebuilt prompt reproduces the recorded hash exactly; otherwise why it could not.</summary>
    internal static string? StaleReasonFor(CapturedDecision decision)
    {
        // A call that carried book text is rebuilt without it, so by definition it cannot claim the original hash.
        if (decision.ExcerptsInvolved) return StaleExcerptsInvolved;
        var run = decision.Run;
        if (run.RenderedMessagesHash is null || run.AppliedMaxOutputTokens is null) return StaleRenderFailed;
        try
        {
            var rendered = TuningCaseRenderer.Render(decision.Role, decision.Input, TuningSource.Household, TuningCoverage.ContractOnly);
            var rebuilt = RenderedMessagesIdentity.Compute(rendered.Messages, true, run.AppliedMaxOutputTokens);
            return string.Equals(rebuilt, run.RenderedMessagesHash, StringComparison.Ordinal) ? null : StaleRenderMismatch;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return StaleRenderFailed;
        }
    }
}
