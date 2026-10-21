using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>Governed synchronous hold review and separately permitted release proposals. Only ApprovalService can approve.</summary>
public sealed class ArtifactDecisionService(
    IModelRoleResolver roles, IModelExecutionPolicy policy, ISemanticFindingStore store, IClock clock,
    IArtifactEvidenceStore? recordedEvidence = null, IModelGatewaySettingsStore? settings = null,
    IArtifactExcerptExtractor? excerpts = null, IModelWorkflowObserver? workflow = null)
{
    public async Task<Guid?> GetReleaseAsync(MediaAsset asset, AssetIdentityVerificationResult result,
        ExpectedArtifactProfile? expected, CancellationToken cancellationToken)
    {
        // Permission and deterministic eligibility precede even role resolution, and hence every model call.
        if (settings is null || recordedEvidence is null || result.IsMatch ||
            !ModelReleasePolicy.IsEligible(asset, result.Facts)) return null;
        try
        {
            if ((await settings.FindAsync(cancellationToken))?.AllowModelHoldRelease != true) return null;
            var resolution = await roles.ResolveAsync(ModelRole.ArtifactValidationDecision, cancellationToken);
            if (!resolution.IsAvailable) return null;
            var rows = await ArtifactReleaseEvidence.ReadAsync(recordedEvidence, asset, cancellationToken);
            var facts = ArtifactReleaseEvidence.Facts(rows);
            if (!ModelReleasePolicy.IsEligible(asset, facts) || result.Facts.Any(f => !facts.Contains(f))) return null;
            var details = !resolution.IsCloud || resolution.AllowCloudExcerpts;
            if (asset.IdentityHoldCode == EvidenceReasonCodes.IdentityHold.CollectionMissingRequestedWork &&
                (!details || expected?.AcceptedAsCollectionContaining != true)) return null;
            var input = ArtifactReleaseEvidence.Render(asset, rows, expected, details);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(Math.Min(resolution.Limits.TimeoutSeconds, 45)));
            ModelExecution<ArtifactDecision> execution;
            try
            {
                execution = await policy.ExecuteAsync(new ModelExecutionRequest(ModelRole.ArtifactValidationDecision,
                    ModelBudgetClass.BackgroundValidation, null,
                    [new ModelChatMessage("system", ArtifactReleaseEvidence.SystemPrompt), new ModelChatMessage("user", input.Text)],
                    JsonObjectOutput: true, MaxOutputTokens: 400, IncludesFileDerivedDetail: input.IncludesDetails, MediaAssetId: asset.Id,
                    DiagnosticContext: EvidenceContext(input.Findings)),
                    text => SemanticAssessmentParser.ParseArtifactRelease(text, asset, facts, input), limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                execution = new(ModelCallOutcome.Timeout, null, "Artifact release review timed out.", null, 0, resolution.Model, resolution.IsCloud);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                execution = new(ModelCallOutcome.TransportFailure, null, "Artifact release review failed.", null, 0, resolution.Model, resolution.IsCloud);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var decision = execution.Succeeded ? execution.Value : null;
            var finding = new SemanticFinding(SemanticSubjectKind.ArtifactDecision, asset.Id.ToString("N"), null, null,
                SemanticAssessmentParser.ArtifactDecisionSchema, input.Digest, asset.IdentityHoldCode!, ModelRole.ArtifactValidationDecision,
                execution.Model ?? resolution.Model ?? "unknown", execution.IsCloud, execution.Outcome, decision?.Verdict,
                decision is null ? null : CandidateSemanticAnalyzer.SerializeClaims(new(SemanticAssessmentParser.ArtifactDecisionSchema,
                    SemanticVerdict.Uncertain, decision.Claims)), execution.Succeeded ? null : execution.Detail, clock.UtcNow, execution.UsageRecordId);
            await store.AddAsync(finding, cancellationToken);
            if (decision?.Verdict == "uncertain" && workflow is not null)
                await workflow.ObserveAsync(execution.UsageRecordId, ModelWorkflowEffect.Abstained, cancellationToken);
            var freshRows = await ArtifactReleaseEvidence.ReadAsync(recordedEvidence, asset, cancellationToken);
            if ((await settings.FindAsync(cancellationToken))?.AllowModelHoldRelease != true ||
                !ModelReleasePolicy.IsEligible(asset, ArtifactReleaseEvidence.Facts(freshRows)) ||
                ArtifactReleaseEvidence.Render(asset, freshRows, expected, details).Digest != input.Digest) return null;
            return decision?.Verdict is "release" or "contains_requested" ? finding.Id : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    public async Task<ArtifactModelHold?> GetHoldAsync(MediaAsset asset, AssetIdentityVerificationResult result,
        ExpectedArtifactProfile? expected, CancellationToken cancellationToken)
    {
        if (!result.IsMatch || asset.Origin is not (AssetOrigin.ExternalProvider or AssetOrigin.BuiltInProvider) ||
            asset.Format.TrimStart('.').ToUpperInvariant() is not ("EPUB" or "KEPUB" or "M4B" or "MP3" or "OGG") ||
            HasHardNegative(result.Facts)) return null;
        try
        {
            var resolution = await roles.ResolveAsync(ModelRole.ArtifactValidationDecision, cancellationToken);
            if (!resolution.IsAvailable) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(Math.Min(resolution.Limits.TimeoutSeconds, 45)));
            IReadOnlyList<ArtifactEvidence> prior = [];
            if (recordedEvidence is not null &&
                (await recordedEvidence.ListLatestAsync([asset.Id], limit.Token)).TryGetValue(asset.Id, out var rows))
                prior = rows.Where(r => r.Sha256 == asset.Sha256 && r.RecordKind is
                    ArtifactEvidenceRecordKinds.SecurityEvaluation or ArtifactEvidenceRecordKinds.TechnicalInspection).ToArray();
            if (prior.Any(r => r.Dimension is EvidenceDimension.Security or EvidenceDimension.Structure && r.Polarity == EvidencePolarity.Negative)) return null;
            var details = !resolution.IsCloud || resolution.AllowCloudExcerpts;
            IReadOnlyList<ArtifactExcerpt> bookText = [];
            if (excerpts is not null && details && asset.Format.TrimStart('.').ToUpperInvariant() is "EPUB" or "KEPUB")
            {
                try { bookText = await excerpts.ExtractAsync(asset, limit.Token); }
                catch (Exception) when (!limit.IsCancellationRequested) { bookText = []; }
            }
            limit.Token.ThrowIfCancellationRequested();
            var input = ArtifactDecisionEvidence.Render(asset, result.Facts, expected, prior, details, excerpts: bookText);
            ArtifactDecision? decision;
            Guid? usageRecordId;
            if (await store.FindLatestAsync(SemanticSubjectKind.ArtifactDecision, input.Digest, limit.Token) is { IsReusable: true } cached &&
                clock.UtcNow - cached.CreatedAtUtc < CandidateSemanticAnalyzer.ReuseWindow)
            {
                var claims = CandidateSemanticAnalyzer.DeserializeClaims(cached.ClaimsJson);
                var reply = JsonSerializer.Serialize(new { schema = SemanticAssessmentParser.ArtifactDecisionSchema, verdict = cached.Verdict,
                    claims = claims.Select(c => new { kind = c.Kind, evidence_ids = c.EvidenceIds, note = c.Note }) });
                decision = cached.IsUsable ? SemanticAssessmentParser.ParseArtifactDecision(reply, input.Findings, bookText).Value : null;
                usageRecordId = cached.ModelUsageRecordId;
            }
            else
            {
                ModelExecution<ArtifactDecision> execution;
                try
                {
                    execution = await policy.ExecuteAsync(new ModelExecutionRequest(ModelRole.ArtifactValidationDecision,
                        ModelBudgetClass.BackgroundValidation, null,
                        [new ModelChatMessage("system", ArtifactDecisionEvidence.SystemPrompt + (bookText.Count > 0 ? ArtifactDecisionEvidence.ExcerptInstructions : "")), new ModelChatMessage("user", input.Text)],
                        JsonObjectOutput: true, MaxOutputTokens: 400, IncludesFileDerivedDetail: input.IncludesDetails, MediaAssetId: asset.Id,
                    DiagnosticContext: EvidenceContext(input.Findings)),
                        text => SemanticAssessmentParser.ParseArtifactDecision(text, input.Findings, bookText), limit.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    execution = new(ModelCallOutcome.Timeout, null, "Artifact review timed out.", null, 0, resolution.Model, resolution.IsCloud);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    execution = new(ModelCallOutcome.TransportFailure, null, "Artifact review failed.", null, 0, resolution.Model, resolution.IsCloud);
                }
                cancellationToken.ThrowIfCancellationRequested();
                decision = execution.Succeeded ? execution.Value : null;
                usageRecordId = execution.UsageRecordId;
                await store.AddAsync(new SemanticFinding(SemanticSubjectKind.ArtifactDecision, asset.Id.ToString("N"), null, null,
                    SemanticAssessmentParser.ArtifactDecisionSchema, input.Digest, "Match", ModelRole.ArtifactValidationDecision,
                    execution.Model ?? resolution.Model ?? "unknown", execution.IsCloud, execution.Outcome, decision?.Verdict,
                    decision is null ? null : CandidateSemanticAnalyzer.SerializeClaims(new(SemanticAssessmentParser.ArtifactDecisionSchema,
                        SemanticVerdict.Uncertain, decision.Claims)), execution.Succeeded ? null : bookText.Count > 0
                            ? "Artifact review did not return a usable decision." : execution.Detail, clock.UtcNow, execution.UsageRecordId), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (limit.IsCancellationRequested) return null;
            if (decision?.Verdict is "no_objection" or "uncertain" && workflow is not null)
                await workflow.ObserveAsync(usageRecordId, decision.Verdict == "uncertain" ? ModelWorkflowEffect.Abstained : ModelWorkflowEffect.NoChange, cancellationToken);
            return decision?.Verdict == "hold"
                ? new ArtifactModelHold(decision.Claims.First(c => c.Stance is ClaimStance.Contradicts or ClaimStance.Concern).Note, usageRecordId) : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An unrecorded or unavailable model decision cannot change the deterministic verdict.
            return null;
        }
    }

    /// <summary>Evidence ids with polarity and reason code only, never values, so a diagnostic shows what the model could cite.</summary>
    private static string EvidenceContext(IReadOnlyList<EvidenceFinding> findings) =>
        string.Join(';', findings.Select(f => $"{f.Id}|{f.Polarity}|{f.Code}"));

    private static bool HasHardNegative(IReadOnlyList<IdentityFact> facts) => facts.Any(f =>
        f.Dimension is EvidenceDimension.Security or EvidenceDimension.Structure && f.Polarity == EvidencePolarity.Negative);
}

