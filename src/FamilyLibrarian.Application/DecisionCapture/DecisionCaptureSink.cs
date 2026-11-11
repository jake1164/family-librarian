using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.DecisionCapture;

/// <summary>How a model call ran, taken from the execution result. Never carries a prompt, a reply or an excerpt.</summary>
/// <param name="UsageRecordId">The ledger row; capture ignores a result with none, because then no model was called.</param>
/// <param name="AppliedMaxOutputTokens">The cap that was sent, so the recorded hash can be reproduced after the limit changes.</param>
/// <param name="Answer">A short normalized answer (verdict or move), or null when the reply was not accepted.</param>
public sealed record DecisionCaptureRun(
    Guid? UsageRecordId, Guid? FindingId, Guid? SettingsId, bool IsCloud, string ModelAlias, string? ReportedModel,
    ModelReasoningMode Reasoning, string? RenderedMessagesHash, int? AppliedMaxOutputTokens, string ParserVersion,
    ModelCallOutcome Outcome, ModelFailureReason? FailureReason, string? Answer);

public static class DecisionCaptureRuns
{
    /// <summary>The run identity of a finished call. <paramref name="answer"/> is a short normalized verdict or move, never a reply.</summary>
    public static DecisionCaptureRun From<T>(ModelExecution<T> execution, Guid? findingId, string parserVersion, string? answer)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new DecisionCaptureRun(
            execution.UsageRecordId, findingId, execution.SettingsId, execution.IsCloud, execution.Model ?? "unknown", execution.ReportedModel,
            execution.Reasoning, execution.RenderedMessagesHash, execution.AppliedMaxOutputTokens, parserVersion,
            execution.Outcome, execution.FailureReason, answer);
    }
}

/// <summary>Maps a snapshot's local candidate key to the provider ids needed to attach an outcome. Never part of the snapshot.</summary>
public sealed record CapturedCandidateLink(string CandidateKey, string ProviderId, string ProviderResultId);

/// <param name="SubjectReference">The same convention as the finding's subject, so two requests with equal content stay separate.</param>
/// <param name="ExcerptsInvolved">True when book excerpts were sent; they are never stored and the hash cannot be reproduced without them.</param>
public sealed record CapturedDecision(
    ModelRole Role, ITuningInput Input, string SubjectReference, Guid? RequestId, Guid? UserId, Guid? MediaAssetId,
    bool ExcerptsInvolved, DecisionCaptureRun Run, IReadOnlyList<CapturedCandidateLink>? Links = null);

/// <summary>
/// Where services report a finished decision (A6-TUNER-C1). Called after the decision is final. It must never throw and never
/// change anything the caller does: a failure is swallowed and logged without content.
/// </summary>
public interface IDecisionCaptureSink
{
    Task CaptureAsync(CapturedDecision decision, CancellationToken cancellationToken);
}

/// <summary>A snapshot and the call made against it, ready to store.</summary>
public sealed record DecisionCaptureWrite(
    FamilyLibrarian.Domain.DecisionCapture.DecisionSnapshot Snapshot, FamilyLibrarian.Domain.DecisionCapture.DecisionCall Call,
    IReadOnlyList<CapturedCandidateLink> Links);

/// <summary>Storage for captured decisions. Uses its own short-lived context so a failure cannot disturb the caller's work.</summary>
public interface IDecisionCaptureWriter
{
    Task WriteAsync(DecisionCaptureWrite write, CancellationToken cancellationToken);
}

/// <summary>Diagnostics for capture. Application has no logging dependency, so this is a port. Implementations must never log content.</summary>
public interface IDecisionCaptureLog
{
    void TooLarge(ModelRole role);

    void Failed(ModelRole? role, string exceptionType);
}
