using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>A governed pre-approval review that returns only an additional hold, never an approval or release.</summary>
public sealed class ArtifactDecisionService(
    IModelRoleResolver roles, IModelExecutionPolicy policy, ISemanticFindingStore store, IClock clock,
    IArtifactEvidenceStore? recordedEvidence = null)
{
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
            IReadOnlyList<ArtifactEvidence> prior = [];
            if (recordedEvidence is not null &&
                (await recordedEvidence.ListLatestAsync([asset.Id], cancellationToken)).TryGetValue(asset.Id, out var rows))
                prior = rows.Where(r => r.Sha256 == asset.Sha256 && r.RecordKind is
                    ArtifactEvidenceRecordKinds.SecurityEvaluation or ArtifactEvidenceRecordKinds.TechnicalInspection).ToArray();
            if (prior.Any(r => r.Dimension is EvidenceDimension.Security or EvidenceDimension.Structure && r.Polarity == EvidencePolarity.Negative)) return null;
            var input = ArtifactDecisionEvidence.Render(asset, result.Facts, expected, prior, !resolution.IsCloud || resolution.AllowCloudExcerpts);
            ArtifactDecision? decision;
            if (await store.FindLatestAsync(SemanticSubjectKind.ArtifactDecision, input.Digest, cancellationToken) is { IsReusable: true } cached &&
                clock.UtcNow - cached.CreatedAtUtc < CandidateSemanticAnalyzer.ReuseWindow)
            {
                var claims = CandidateSemanticAnalyzer.DeserializeClaims(cached.ClaimsJson);
                var reply = JsonSerializer.Serialize(new { schema = SemanticAssessmentParser.ArtifactDecisionSchema, verdict = cached.Verdict,
                    claims = claims.Select(c => new { kind = c.Kind, evidence_ids = c.EvidenceIds, note = c.Note }) });
                decision = cached.IsUsable ? SemanticAssessmentParser.ParseArtifactDecision(reply, input.Findings).Value : null;
            }
            else
            {
                ModelExecution<ArtifactDecision> execution;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(Math.Min(resolution.Limits.TimeoutSeconds, 45)));
                try
                {
                    execution = await policy.ExecuteAsync(new ModelExecutionRequest(ModelRole.ArtifactValidationDecision,
                        ModelBudgetClass.BackgroundValidation, null,
                        [new ModelChatMessage("system", ArtifactDecisionEvidence.SystemPrompt), new ModelChatMessage("user", input.Text)],
                        JsonObjectOutput: true, MaxOutputTokens: 400, IncludesFileDerivedDetail: input.IncludesDetails),
                        text => SemanticAssessmentParser.ParseArtifactDecision(text, input.Findings), limit.Token);
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
                await store.AddAsync(new SemanticFinding(SemanticSubjectKind.ArtifactDecision, asset.Id.ToString("N"), null, null,
                    SemanticAssessmentParser.ArtifactDecisionSchema, input.Digest, "Match", ModelRole.ArtifactValidationDecision,
                    execution.Model ?? resolution.Model ?? "unknown", execution.IsCloud, execution.Outcome, decision?.Verdict,
                    decision is null ? null : CandidateSemanticAnalyzer.SerializeClaims(new(SemanticAssessmentParser.ArtifactDecisionSchema,
                        SemanticVerdict.Uncertain, decision.Claims)), execution.Succeeded ? null : execution.Detail, clock.UtcNow), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return decision?.Verdict == "hold"
                ? new ArtifactModelHold(decision.Claims.First(c => c.Stance is ClaimStance.Contradicts or ClaimStance.Concern).Note) : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An unrecorded or unavailable model decision cannot change the deterministic verdict.
            return null;
        }
    }

    private static bool HasHardNegative(IReadOnlyList<IdentityFact> facts) => facts.Any(f =>
        f.Dimension is EvidenceDimension.Security or EvidenceDimension.Structure && f.Polarity == EvidencePolarity.Negative);
}

