using System.Text.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.Evidence;
using static FamilyLibrarian.Application.SemanticAssessment.SemanticJson;

namespace FamilyLibrarian.Application.SemanticAssessment;

public sealed record CandidateTriage(string Move, string? Candidate, IReadOnlyList<string> EvidenceIds, string Note);

public static partial class SemanticAssessmentParser
{
    public const string CandidateTriageSchema = "candidate-triage/1";
    private static readonly string[] TriageProperties = ["schema", "move", "candidate", "evidence_ids", "note"];

    public static ModelParseResult<CandidateTriage> ParseCandidateTriage(string? content,
        IReadOnlyDictionary<string, IReadOnlyList<EvidenceFinding>> supplied)
    {
        if (string.IsNullOrWhiteSpace(content)) return ModelParse.NotJson<CandidateTriage>("empty response");
        if (content.Length > MaxContentLength) return RejectTriage("response is too long");
        JsonDocument document;
        try { document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException) { return ModelParse.NotJson<CandidateTriage>("not valid JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, TriageProperties) ||
                !IsString(root.GetProperty("schema"), out var schema) || schema != CandidateTriageSchema)
                return RejectTriage("expected exact candidate-triage/1 object");
            if (!IsString(root.GetProperty("move"), out var move) ||
                move is not ("keep" or "veto" or "prefer" or "author_variant" or "uncertain")) return RejectTriage("unknown move");
            var target = root.GetProperty("candidate");
            string? candidate = null;
            if (move is "keep" or "uncertain")
            {
                if (target.ValueKind != JsonValueKind.Null) return RejectTriage("non-moves require null candidate");
            }
            else if (!IsString(target, out candidate) || !supplied.ContainsKey(candidate)) return RejectTriage("unknown candidate id");
            var evidence = root.GetProperty("evidence_ids");
            if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > MaxEvidenceIdsPerClaim)
                return RejectTriage("too many evidence ids");
            var cited = new List<EvidenceFinding>();
            foreach (var id in evidence.EnumerateArray())
            {
                if (!IsString(id, out var key) || cited.Any(f => f.Id == key)) return RejectTriage("invalid or duplicate evidence id");
                var finding = (candidate is null ? supplied.Values.SelectMany(v => v) : supplied[candidate]).FirstOrDefault(f => f.Id == key);
                if (finding is null) return RejectTriage("evidence must belong to the supplied candidate");
                cited.Add(finding);
            }
            if (!IsPlainNote(root.GetProperty("note"), out var note, out var error)) return RejectTriage(error!);
            var result = new CandidateTriage(move, candidate, cited.Select(f => f.Id).ToArray(), note);
            return IsGroundedTriage(result, cited) ? ModelParse.Ok(result) : RejectTriage("move lacks supporting evidence");
        }
    }

    internal static bool IsGroundedTriage(CandidateTriage move, IReadOnlyList<EvidenceFinding> cited) => move.Move switch
    {
        "keep" or "uncertain" => move.Candidate is null,
        "veto" => cited.Any(f => f.Polarity is EvidencePolarity.Negative or EvidencePolarity.Condition),
        "prefer" => cited.Any(f => f.Polarity == EvidencePolarity.Positive),
        "author_variant" => cited.Any(f => f.Code is "candidate.title.exact" or "candidate.title.compatible" && f.Polarity == EvidencePolarity.Positive) &&
            (cited.Any(f => f.Code is "candidate.series.exact" or EvidenceReasonCodes.CandidateCorroboration.Identifier && f.Polarity == EvidencePolarity.Positive) ||
             cited.Any(f => f.Code == EvidenceReasonCodes.CandidateCorroboration.Year && f.Polarity == EvidencePolarity.Positive) &&
             cited.Any(f => f.Code == EvidenceReasonCodes.CandidateCorroboration.Publisher && f.Polarity == EvidencePolarity.Positive)),
        _ => false
    };

    private static ModelParseResult<CandidateTriage> RejectTriage(string detail) => ModelParse.SchemaRejected<CandidateTriage>(detail);
}
