using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.SemanticAssessment;

public sealed record CandidateTriageResult(string Move, FulfillmentOption Candidate, CandidateTriageAuthorization? Authorization, Guid? UsageRecordId = null);

/// <summary>One governed comparison per provider pass; persisted decisions are re-grounded before reuse.</summary>
public sealed class CandidateTriageService(IModelRoleResolver roles, IModelExecutionPolicy policy, ISemanticFindingStore store, IClock clock,
    IModelWorkflowObserver? workflow = null)
{
    public Task ObserveSelectionAsync(CandidateTriageResult result, CancellationToken cancellationToken) =>
        workflow?.ObserveAsync(result.UsageRecordId, result.Move == "veto" ? ModelWorkflowEffect.CandidateVetoUsed :
            ModelWorkflowEffect.CandidateSelectionUsed, cancellationToken) ?? Task.CompletedTask;

    public async Task<CandidateTriageResult?> GetAsync(Guid requestId, Guid formatId, string providerId,
        CandidateTriageContext context, CancellationToken cancellationToken)
    {
        if (context.Pick is null && context.TiedResultIds.Count < 2 && !context.Candidates.Any(CandidateTriageRules.IsAuthorOnlyConflict))
            return null;
        try
        {
            if (!(await roles.ResolveAsync(ModelRole.CandidateTriage, cancellationToken)).IsAvailable) return null;
            var input = CandidateTriageEvidence.Render(context);
            ModelParseResult<CandidateTriage> Parse(string? text)
            {
                var parsed = SemanticAssessmentParser.ParseCandidateTriage(text, input.Findings);
                return parsed.IsValid && !CandidateTriageRules.Allows(parsed.Value!, context, input)
                    ? ModelParse.SchemaRejected<CandidateTriage>("move not allowed by current candidate rules", ModelFailureReason.DisallowedAction) : parsed;
            }
            CandidateTriage? decision;
            Guid? usageRecordId;
            if (await store.FindLatestAsync(SemanticSubjectKind.CandidateTriage, input.Digest, cancellationToken) is { IsReusable: true } cached &&
                clock.UtcNow - cached.CreatedAtUtc < CandidateSemanticAnalyzer.ReuseWindow)
            {
                decision = cached.IsUsable ? Parse(cached.ClaimsJson).Value : null;
                usageRecordId = cached.ModelUsageRecordId;
            }
            else
            {
                ModelExecution<CandidateTriage> execution;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    execution = await policy.ExecuteAsync(new ModelExecutionRequest(ModelRole.CandidateTriage,
                        ModelBudgetClass.BackgroundValidation, null,
                        [new ModelChatMessage("system", CandidateTriageEvidence.SystemPrompt), new ModelChatMessage("user", input.Text)],
                        JsonObjectOutput: true, MaxOutputTokens: 300), Parse, limit.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    execution = new(ModelCallOutcome.Timeout, null, "Candidate triage timed out.", null, 0, null, false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    execution = new(ModelCallOutcome.TransportFailure, null, "Candidate triage failed.", null, 0, null, false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                decision = execution.Succeeded ? execution.Value : null;
                usageRecordId = execution.UsageRecordId;
                var claims = decision is null ? null : JsonSerializer.Serialize(new
                    { schema = SemanticAssessmentParser.CandidateTriageSchema, move = decision.Move, candidate = decision.Candidate,
                      evidence_ids = decision.EvidenceIds, note = decision.Note });
                await store.AddAsync(new SemanticFinding(SemanticSubjectKind.CandidateTriage,
                    formatId.ToString("N") + ":" + providerId, requestId, providerId, SemanticAssessmentParser.CandidateTriageSchema,
                    input.Digest, context.Pick is null ? "NoAutomaticPick" : "AutomaticPick", ModelRole.CandidateTriage,
                    execution.Model ?? "unknown", execution.IsCloud, execution.Outcome, decision?.Move, claims,
                    execution.Succeeded ? null : execution.Detail, clock.UtcNow, execution.UsageRecordId), cancellationToken);
            }
            if (decision?.Move is "keep" or "uncertain" && workflow is not null)
                await workflow.ObserveAsync(usageRecordId, decision.Move == "uncertain" ? ModelWorkflowEffect.Abstained : ModelWorkflowEffect.NoChange, cancellationToken);
            if (decision?.Candidate is not { } id || !input.Candidates.TryGetValue(id, out var option) ||
                !CandidateTriageRules.Allows(decision, context, input)) return null;
            return new(decision.Move, option, decision.Move is "author_variant" or "prefer"
                ? new CandidateTriageAuthorization(requestId, formatId, option, decision.Move,
                    decision.Move == "prefer" ? context.TiedResultIds.Where(id => id != option.ProviderResultId).ToArray() : []) : null, usageRecordId);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Role/storage failures are deterministic fallback, never an unrecorded permission.
            return null;
        }
    }
}
