using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningCorpusRole(ModelRole Role, string Resource, int CaseCount);
public sealed record TuningCorpusManifest(int SchemaVersion, string CorpusVersion, string LabelVersion, IReadOnlyList<TuningCorpusRole> Roles);
public sealed record TuningCorpusCase(TuningCase Case, string PromptIdentity, IReadOnlyList<string> ReferenceReplies);
public sealed record BuiltInTuningCorpus(TuningCorpusManifest Manifest, IReadOnlyList<TuningCorpusCase> Cases)
{
    public TuningCalibrationKey CalibrationKey(ModelRole role)
    {
        if (!TuningRoles.IsScored(role)) throw new ArgumentException("Role is not scored.", nameof(role));
        var prompts = Cases.Where(c => c.Case.Role == role).OrderBy(c => c.Case.Id, StringComparer.Ordinal)
            .Select(c => new { c.Case.Id, c.PromptIdentity }).ToArray();
        var identity = JsonSerializer.Serialize(new { contract = TuningContractIdentity.Current, role, prompts }, TuningInputJson.Options);
        return new(Manifest.CorpusVersion, Manifest.LabelVersion, Manifest.SchemaVersion,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    public static BuiltInTuningCorpus LoadEmbedded()
    {
        var assembly = typeof(BuiltInTuningCorpus).Assembly;
        string Read(string name)
        {
            using var stream = assembly.GetManifestResourceStream("FamilyLibrarian.ModelTuning." + name)
                ?? throw new InvalidOperationException("Missing built-in corpus resource.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        return Load(Read("manifest.json"), Read);
    }

    public static BuiltInTuningCorpus Load(string manifestJson, Func<string, string> readResource)
    {
        ArgumentNullException.ThrowIfNull(readResource);
        using var manifestDocument = JsonDocument.Parse(manifestJson);
        TuningInputJson.CheckTree(manifestDocument.RootElement);
        var manifest = manifestDocument.RootElement.Deserialize<TuningCorpusManifest>(TuningInputJson.Options)
            ?? throw new JsonException("Missing manifest.");
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.CorpusVersion) || string.IsNullOrWhiteSpace(manifest.LabelVersion) ||
            !manifest.Roles.Select(r => r.Role).Order().SequenceEqual(TuningRoles.Scored.Order())) throw new JsonException("Invalid corpus manifest.");
        var cases = new List<TuningCorpusCase>();
        foreach (var role in manifest.Roles)
        {
            var requiredCount = role.Role is ModelRole.ArtifactValidationDecision or ModelRole.CandidateTriage ? 60 : 40;
            if (role.CaseCount != requiredCount || role.Resource != role.Role + ".json") throw new JsonException("Invalid role resource/count.");
            using var document = JsonDocument.Parse(readResource(role.Resource), new JsonDocumentOptions { MaxDepth = 24 });
            TuningInputJson.CheckTree(document.RootElement);
            var wires = document.RootElement.Deserialize<CaseWire[]>(TuningInputJson.Options) ?? throw new JsonException("Missing cases.");
            if (wires.Length != requiredCount) throw new JsonException("Corpus count differs from manifest.");
            var start = cases.Count;
            foreach (var wire in wires)
            {
                if (wire.SchemaVersion != 1 || wire.Source != TuningSource.BuiltIn || wire.Role != role.Role || wire.Coverage != TuningCoverage.ProductionReachable ||
                    string.IsNullOrWhiteSpace(wire.Id) || string.IsNullOrWhiteSpace(wire.Rationale) || wire.Expected.Acceptable.Count == 0 ||
                    wire.Expected.Acceptable.Any(a => !ValidLabel(role.Role, a)) || wire.Review.LabelRevision < 1 ||
                    wire.Expected.RequiredEvidenceAlternatives.Any(ids => ids.Count == 0) || wire.Expected.Forbidden.Any(f => f is not ("prefer_current" or "inconsistent_without_negative")))
                    throw new JsonException("Invalid built-in case/label.");
                var input = TuningInputJson.Read(role.Role, wire.Input.GetRawText());
                var item = new TuningCase(wire.SchemaVersion, wire.Source, wire.Id, wire.Role, wire.Category, wire.Coverage,
                    wire.Rationale, wire.Review, input, wire.Expected, wire.SyntheticSupplement);
                cases.Add(new(item, wire.PromptIdentity, wire.ReferenceReplies));
            }
            var roleCases = cases.Skip(start).Select(c => c.Case).ToArray();
            if (roleCases.Count(TuningScoring.RequiresNonAbstention) * 10 < requiredCount * 4)
                throw new JsonException("Too few mandatory non-abstention cases.");
            if (role.Role == ModelRole.CandidateAmbiguityAssessment && roleCases.Count(c => c.Expected.Acceptable.All(a => a.Verdict == "uncertain")) * 10 < requiredCount * 4)
                throw new JsonException("Too few uncertain ambiguity cases.");
        }
        if (cases.Select(c => c.Case.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count) throw new JsonException("Duplicate corpus case id.");
        return new(manifest, cases);
    }

    private static bool ValidLabel(ModelRole role, TuningAnswer answer) => role switch
    {
        ModelRole.ArtifactValidationAssessment or ModelRole.CandidateAmbiguityAssessment => answer.Verdict is "consistent" or "inconsistent" or "uncertain",
        ModelRole.ArtifactValidationDecision => answer.Verdict is "hold" or "no_objection" or "uncertain",
        ModelRole.CandidateTriage => answer.Move is "keep" or "uncertain" ? answer.TargetKey is null :
            answer.Move is "veto" or "prefer" or "author_variant" && !string.IsNullOrWhiteSpace(answer.TargetKey),
        ModelRole.IntentInterpretation => answer.Intent is "check_status" or "out_of_domain" or "unclear" ? answer.Title is null && answer.Author is null :
            answer.Intent == "find_book" && !string.IsNullOrWhiteSpace(answer.Title),
        ModelRole.SearchGuidance => answer.GiveUp == true || answer.GiveUp == false && answer.AllowedSuggestions is { Count: > 0 },
        _ => false
    };

    private sealed record CaseWire(int SchemaVersion, TuningSource Source, string Id, ModelRole Role, TuningLabelKind Category,
        TuningCoverage Coverage, string Rationale, LabelReview Review, JsonElement Input, TuningExpected Expected,
        SyntheticSupplement? SyntheticSupplement, string PromptIdentity, IReadOnlyList<string> ReferenceReplies);
}
