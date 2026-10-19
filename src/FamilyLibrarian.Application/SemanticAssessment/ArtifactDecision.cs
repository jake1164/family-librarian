using System.Globalization;
using System.Text.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.Evidence;
using static FamilyLibrarian.Application.SemanticAssessment.SemanticJson;

namespace FamilyLibrarian.Application.SemanticAssessment;

public sealed record ArtifactDecision(string Verdict, IReadOnlyList<SemanticClaim> Claims);
/// <summary>The only action a validation model can propose. Approval and release are deliberately unrepresentable here.</summary>
public sealed record ArtifactModelHold(string Note);

public static partial class SemanticAssessmentParser
{
    public const string ArtifactDecisionSchema = "artifact-decision/1";

    public static ModelParseResult<ArtifactDecision> ParseArtifactDecision(string? content, IReadOnlyList<EvidenceFinding> evidence)
    {
        if (string.IsNullOrWhiteSpace(content)) return ModelParse.NotJson<ArtifactDecision>("empty response");
        if (content.Length > MaxContentLength) return ModelParse.SchemaRejected<ArtifactDecision>("response is too long");
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, TopLevel) ||
                !IsString(root.GetProperty("schema"), out var schema) || schema != ArtifactDecisionSchema ||
                !IsString(root.GetProperty("verdict"), out var verdict) || verdict is not ("hold" or "no_objection" or "uncertain"))
                return ModelParse.SchemaRejected<ArtifactDecision>("expected artifact-decision/1 with hold, no_objection or uncertain");

            // Reuse the artifact claim contract, including strict keys, citations and contradiction grounding.
            // The decision schema adds concern grounding without changing the advisory parser's behavior.
            var parsed = Parse(JsonSerializer.Serialize(new { schema = SemanticSchema.Artifact.Id, verdict = "uncertain",
                claims = root.GetProperty("claims") }), SemanticSchema.Artifact, evidence);
            if (!parsed.IsValid) return new(null, parsed.Failure, parsed.Detail);
            var claims = parsed.Value!.Claims;
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
