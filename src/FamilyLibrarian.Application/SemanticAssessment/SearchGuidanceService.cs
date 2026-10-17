using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>Governed, persisted search proposals. Every failure leaves deterministic search and acquisition unchanged.</summary>
public sealed class SearchGuidanceService(IModelRoleResolver roles, IModelExecutionPolicy policy, ISemanticFindingStore store, IClock clock)
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        try { return (await roles.ResolveAsync(ModelRole.SearchGuidance, cancellationToken)).IsAvailable; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task<SearchGuidance?> GetAsync(Guid requestId, Guid formatId, BookIdentity requested,
        IReadOnlyList<FulfillmentOption> results, IReadOnlyCollection<string> triedKeys, DateTimeOffset cycleStart,
        CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(cancellationToken)) return null;
        try
        {
            var subject = formatId.ToString("N");
            var latest = await store.FindLatestForSubjectsAsync(SemanticSubjectKind.SearchGuidance, [subject], cancellationToken);
            if (latest.TryGetValue(subject, out var inCycle) && inCycle.CreatedAtUtc >= cycleStart) return null;
            var input = SearchGuidanceEvidence.Render(requested, results, triedKeys);
            ModelParseResult<SearchGuidance> Parse(string? content)
            {
                var parsed = SemanticAssessmentParser.ParseSearchGuidance(content, requested, input.EvidenceIds, triedKeys);
                return parsed.IsValid && SearchGuidanceVariants.Build(requested, parsed.Value!) is null
                    ? ModelParse.SchemaRejected<SearchGuidance>("search proposal cannot be applied to the catalog fields") : parsed;
            }
            if (await store.FindLatestAsync(SemanticSubjectKind.SearchGuidance, input.Digest, cancellationToken) is { IsReusable: true } existing &&
                clock.UtcNow - existing.CreatedAtUtc < CandidateSemanticAnalyzer.ReuseWindow)
                return existing.IsUsable ? Parse(existing.ClaimsJson).Value : null;

            ModelExecution<SearchGuidance> execution;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                execution = await policy.ExecuteAsync(new ModelExecutionRequest(ModelRole.SearchGuidance,
                    ModelBudgetClass.BackgroundValidation, null,
                    [new ModelChatMessage("system", SearchGuidanceEvidence.SystemPrompt), new ModelChatMessage("user", input.Text)],
                    JsonObjectOutput: true, MaxOutputTokens: 400), content => Parse(content), limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                execution = new(ModelCallOutcome.Timeout, null, "Search guidance timed out.", null, 0, null, false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                execution = new(ModelCallOutcome.TransportFailure, null, "Search guidance failed.", null, 0, null, false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var claims = execution.Succeeded && execution.Value is not null ? Serialize(execution.Value) : null;
            if (claims?.Length > SemanticFinding.MaxClaimsJsonLength)
            {
                execution = execution with { Outcome = ModelCallOutcome.OutputTooLarge, Value = null, Detail = "Validated proposals exceed stored bounds." };
                claims = null;
            }
            await store.AddAsync(new SemanticFinding(SemanticSubjectKind.SearchGuidance, subject, requestId, null,
                SemanticAssessmentParser.SearchGuidanceSchema, input.Digest, "NoAllowedCandidate", ModelRole.SearchGuidance,
                execution.Model ?? "unknown", execution.IsCloud, execution.Outcome,
                execution.Succeeded ? execution.Value!.GiveUp ? "GiveUp" : "Search" : null, claims,
                execution.Succeeded ? null : execution.Detail, clock.UtcNow), cancellationToken);
            return execution.Succeeded ? execution.Value : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A storage/role problem cannot turn a proposal into permission or prevent deterministic processing.
            return null;
        }
    }

    private static string Serialize(SearchGuidance guidance) => JsonSerializer.Serialize(new
    {
        schema = SemanticAssessmentParser.SearchGuidanceSchema, give_up = guidance.GiveUp,
        suggestions = guidance.Suggestions.Select(s => new { reason = s.Reason, value = s.Value, evidence_ids = s.EvidenceIds, note = s.Note })
    });
}
