using System.Globalization;
using System.Text.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using static FamilyLibrarian.Application.SemanticAssessment.SemanticJson;

namespace FamilyLibrarian.Application.SemanticAssessment;

public sealed record ArtifactDecision(string Verdict, IReadOnlyList<SemanticClaim> Claims);
/// <summary>The only action a validation model can propose. Approval and release are deliberately unrepresentable here.</summary>
public sealed record ArtifactModelHold(string Note);

public static partial class SemanticAssessmentParser
{
    public const string ArtifactDecisionSchema = "artifact-decision/1";

    public static ModelParseResult<ArtifactDecision> ParseArtifactDecision(string? content, IReadOnlyList<EvidenceFinding> evidence)
        => ParseArtifactDecisionCore(content, evidence, false);

    public static ModelParseResult<ArtifactDecision> ParseArtifactRelease(string? content, MediaAsset asset,
        IReadOnlyList<IdentityFact> facts, ArtifactDecisionInput input)
    {
        if (!ModelReleasePolicy.IsEligible(asset, facts))
            return ModelParse.SchemaRejected<ArtifactDecision>("Hold is not eligible for model release.");
        var parsed = ParseArtifactDecisionCore(content, input.Findings, true);
        if (!parsed.IsValid || parsed.Value!.Verdict == "uncertain") return parsed;
        var decision = parsed.Value;
        var collection = asset.IdentityHoldCode == EvidenceReasonCodes.IdentityHold.CollectionMissingRequestedWork;
        if (decision.Verdict != (collection ? "contains_requested" : "release"))
            return ModelParse.SchemaRejected<ArtifactDecision>("Wrong release verdict for this hold.");
        var grounded = decision.Claims.Where(c => c.Kind == "supports_declared_identity").Any(claim =>
        {
            var cited = input.Findings.Where(f => claim.EvidenceIds.Contains(f.Id, StringComparer.Ordinal))
                .SelectMany(f => facts.Where(actual => f.Id == "idv." + actual.FindingId && actual.Code == f.Code && actual.Polarity == f.Polarity)).ToArray();
            if (collection) return cited.Any(ModelReleasePolicy.IsCollectionContentsFact);
            if (asset.IdentityHoldCode == EvidenceReasonCodes.IdentityHold.ContentContradicted)
                return ModelReleasePolicy.HasAuthorAgreements(cited);
            return cited.Any(ModelReleasePolicy.IsPositiveIdentityFact);
        });
        return grounded ? parsed : ModelParse.SchemaRejected<ArtifactDecision>("Release lacks the required cited identity evidence.");
    }

    private static ModelParseResult<ArtifactDecision> ParseArtifactDecisionCore(string? content,
        IReadOnlyList<EvidenceFinding> evidence, bool release)
    {
        if (string.IsNullOrWhiteSpace(content)) return ModelParse.NotJson<ArtifactDecision>("empty response");
        if (content.Length > MaxContentLength) return ModelParse.SchemaRejected<ArtifactDecision>("response is too long");
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, TopLevel) ||
                !IsString(root.GetProperty("schema"), out var schema) || schema != ArtifactDecisionSchema ||
                !IsString(root.GetProperty("verdict"), out var verdict) ||
                (release ? verdict is not ("release" or "contains_requested" or "uncertain") : verdict is not ("hold" or "no_objection" or "uncertain")))
                return ModelParse.SchemaRejected<ArtifactDecision>("expected artifact-decision/1 with hold, no_objection or uncertain");

            // Reuse the artifact claim contract, including strict keys, citations and contradiction grounding.
            // The decision schema adds concern grounding without changing the advisory parser's behavior.
            var parsed = Parse(JsonSerializer.Serialize(new { schema = SemanticSchema.Artifact.Id, verdict = "uncertain",
                claims = root.GetProperty("claims") }), release ? SemanticSchema.ArtifactRelease : SemanticSchema.Artifact, evidence);
            if (!parsed.IsValid) return new(null, parsed.Failure, parsed.Detail);
            var claims = release ? parsed.Value!.Claims.Select(c => c.Kind == "supports_declared_identity"
                ? c with { Stance = ClaimStance.Supports } : c).ToArray() : parsed.Value!.Claims;
            foreach (var claim in claims)
            {
                if (claim.Note.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                    return ModelParse.SchemaRejected<ArtifactDecision>("note contains invisible formatting");
                if (claim.Stance == ClaimStance.Concern && !claim.EvidenceIds.Any(id => evidence.Any(f => f.Id == id &&
                    f.Polarity is EvidencePolarity.Negative or EvidencePolarity.Weak or EvidencePolarity.Condition)))
                    return ModelParse.SchemaRejected<ArtifactDecision>("concern cites no negative, weak or condition evidence");
            }
            if (verdict == "hold" && !claims.Any(c => c.Stance is ClaimStance.Contradicts or ClaimStance.Concern))
                return ModelParse.SchemaRejected<ArtifactDecision>("hold needs a grounded negative claim");
            return ModelParse.Ok(new ArtifactDecision(verdict, claims));
        }
        catch (JsonException)
        {
            return ModelParse.NotJson<ArtifactDecision>("not valid JSON");
        }
    }
}
