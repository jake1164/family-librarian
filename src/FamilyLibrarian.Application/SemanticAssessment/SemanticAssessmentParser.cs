using static FamilyLibrarian.Application.SemanticAssessment.SemanticJson;
using System.Text.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.SemanticAssessment;

/// <summary>
/// Strict validation of a model's assessment (alpha.6 M4-2). The reply is untrusted text. It is accepted only if it is
/// exactly the versioned JSON shape asked for: no extra or missing field, no unknown enum value, no markdown or wrapper,
/// no invented evidence id, and no claim that the cited evidence cannot back. Anything else is rejected whole, with a short
/// reason, and never partially used.
/// </summary>
public static partial class SemanticAssessmentParser
{
    public const int MaxContentLength = 4_000;
    public const int MaxClaims = 8;
    public const int MaxEvidenceIdsPerClaim = 6;
    public const int MaxNoteLength = 200;
    private const int MaxDepth = 6;

    private static readonly string[] TopLevel = ["schema", "verdict", "claims"];
    private static readonly string[] ClaimProperties = ["kind", "evidence_ids", "note"];

    public static readonly IReadOnlyList<string> Verdicts = ["consistent", "inconsistent", "uncertain"];

    public static ModelParseResult<SemanticAssessment> Parse(string? content, SemanticSchema schema, IReadOnlyList<EvidenceFinding> suppliedEvidence)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(suppliedEvidence);

        if (string.IsNullOrWhiteSpace(content))
        {
            return ModelParse.NotJson<SemanticAssessment>("empty response");
        }

        if (content.Length > MaxContentLength)
        {
            return ModelParse.SchemaRejected<SemanticAssessment>("response is too long");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return ModelParse.NotJson<SemanticAssessment>("not valid JSON (surrounding text or markdown fences are not accepted)");
        }

        using (document)
        {
            return Validate(document.RootElement, schema, suppliedEvidence);
        }
    }

    private static ModelParseResult<SemanticAssessment> Validate(JsonElement root, SemanticSchema schema, IReadOnlyList<EvidenceFinding> evidence)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, TopLevel))
        {
            return Reject($"the object must contain exactly: {string.Join(", ", TopLevel)} (nothing extra, nothing missing)");
        }

        if (!IsString(root.GetProperty("schema"), out var schemaId) || !string.Equals(schemaId, schema.Id, StringComparison.Ordinal))
        {
            return Reject($"schema must be exactly '{schema.Id}'");
        }

        if (!IsString(root.GetProperty("verdict"), out var verdictText) || !Verdicts.Contains(verdictText, StringComparer.Ordinal))
        {
            return Reject("verdict must be one of: " + string.Join(", ", Verdicts));
        }

        var claimsElement = root.GetProperty("claims");
        if (claimsElement.ValueKind != JsonValueKind.Array || claimsElement.GetArrayLength() is < 1 or > MaxClaims)
        {
            return Reject($"claims must be an array of one to {MaxClaims} items");
        }

        var byId = evidence.GroupBy(finding => finding.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var claims = new List<SemanticClaim>();
        foreach (var item in claimsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !HasExactly(item, ClaimProperties))
            {
                return Reject("each claim must be an object with exactly kind, evidence_ids and note");
            }

            if (!IsString(item.GetProperty("kind"), out var kind) || !schema.TryGetStance(kind, out var stance))
            {
                return Reject($"unknown claim kind '{Clip(kind)}'", ModelFailureReason.UnknownClaim);
            }

            var ids = item.GetProperty("evidence_ids");
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > MaxEvidenceIdsPerClaim)
            {
                return Reject($"evidence_ids must be an array of at most {MaxEvidenceIdsPerClaim} ids");
            }

            var cited = new List<EvidenceFinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var idElement in ids.EnumerateArray())
            {
                if (!IsString(idElement, out var id) || !byId.TryGetValue(id, out var finding))
                {
                    return Reject($"a claim cites an evidence id that was not supplied ('{Clip(idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null)}')", ModelFailureReason.UnknownEvidence);
                }

                if (!seen.Add(id))
                {
                    return Reject($"a claim cites evidence id '{id}' more than once", ModelFailureReason.DuplicateEvidence);
                }

                cited.Add(finding);
            }

            if (GroundingError(kind, stance, cited) is { } grounding)
            {
                return Reject(grounding.Detail, grounding.Reason);
            }

            if (!IsPlainNote(item.GetProperty("note"), out var note, out var noteError))
            {
                return Reject(noteError!);
            }

            claims.Add(new SemanticClaim(kind, stance, [.. cited.Select(finding => finding.Id)], note));
        }

        var verdict = verdictText switch
        {
            "consistent" => SemanticVerdict.Consistent,
            "inconsistent" => SemanticVerdict.Inconsistent,
            _ => SemanticVerdict.Uncertain
        };
        var supports = claims.Any(claim => claim.Stance == ClaimStance.Supports);
        var contradicts = claims.Any(claim => claim.Stance == ClaimStance.Contradicts);
        if (verdict == SemanticVerdict.Consistent && (!supports || contradicts))
        {
            return Reject("a consistent verdict needs a supporting claim and no contradicting claim", ModelFailureReason.UngroundedVerdict);
        }

        if (verdict == SemanticVerdict.Inconsistent && !contradicts)
        {
            return Reject("an inconsistent verdict needs a contradicting claim", ModelFailureReason.UngroundedVerdict);
        }

        return ModelParse.Ok(new SemanticAssessment(schema.Id, verdict, claims));
    }

    /// <summary>A claim may only lean on evidence that could actually back it. Null when it can.</summary>
    private static (string Detail, ModelFailureReason Reason)? GroundingError(string kind, ClaimStance stance, List<EvidenceFinding> cited)
    {
        if (cited.Count == 0)
        {
            return kind == SemanticSchema.InsufficientInformation ? null : ($"claim '{kind}' cites no evidence", ModelFailureReason.NoCitedEvidence);
        }

        return stance switch
        {
            ClaimStance.Supports when !cited.Any(finding => finding.Polarity is EvidencePolarity.Positive or EvidencePolarity.Weak)
                => ($"claim '{kind}' cites no supporting evidence", ModelFailureReason.NoSupportingEvidence),
            ClaimStance.Contradicts when !cited.Any(finding => finding.Polarity == EvidencePolarity.Negative)
                => ($"claim '{kind}' cites no contradicting evidence", ModelFailureReason.NoContradictingEvidence),
            _ => null
        };
    }

}
