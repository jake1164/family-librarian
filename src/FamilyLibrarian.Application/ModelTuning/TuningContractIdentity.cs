using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningContractSnapshot(int SchemaVersion, IReadOnlyDictionary<string, string> Sources);

/// <summary>Build-time contract fingerprint checked against source by tests; no production filesystem access.</summary>
public static class TuningContractIdentity
{
    public static TuningContractSnapshot Snapshot { get; } = Read();

    /// <summary>Whole-contract hash over every fingerprinted source. Diagnostic only; calibration uses <see cref="ForRole(ModelRole)"/>.</summary>
    public static string Current { get; } = Identity("tuning-contract/1:", Snapshot.Sources);

    /// <summary>
    /// Production files that only one role's prompt, parser or rules can reach. A change to one of them must not invalidate the
    /// calibration of any other role. Anything not listed here is shared and belongs to every role's identity, so a mistaken omission
    /// over-invalidates and never under-invalidates. A file may be listed only after checking that no other role's fingerprinted
    /// source refers to its types.
    /// </summary>
    internal static IReadOnlyDictionary<ModelRole, string[]> RoleExclusiveSources { get; } = new Dictionary<ModelRole, string[]>
    {
        [ModelRole.ArtifactValidationDecision] =
        [
            "src/FamilyLibrarian.Application/SemanticAssessment/ArtifactDecision.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/ArtifactDecisionEvidence.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/ArtifactDecisionService.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/ArtifactExcerptEvidence.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/ArtifactReleaseEvidence.cs"
        ],
        [ModelRole.CandidateTriage] =
        [
            "src/FamilyLibrarian.Application/SemanticAssessment/CandidateTriage.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/CandidateTriageEvidence.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/CandidateTriageRules.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/CandidateTriageService.cs"
        ],
        [ModelRole.SearchGuidance] =
        [
            "src/FamilyLibrarian.Application/SemanticAssessment/SearchGuidance.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/SearchGuidanceEvidence.cs",
            "src/FamilyLibrarian.Application/SemanticAssessment/SearchGuidanceService.cs"
        ],
        [ModelRole.IntentInterpretation] = ["src/FamilyLibrarian.Application/Conversation/BookIntentInterpreter.cs"]
    };

    /// <summary>The contract identity for one role: every shared source plus only that role's own exclusive sources.</summary>
    public static string ForRole(ModelRole role) => ForRole(role, Snapshot.Sources);

    internal static string ForRole(ModelRole role, IReadOnlyDictionary<string, string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var otherRoles = RoleExclusiveSources.Where(pair => pair.Key != role).SelectMany(pair => pair.Value).ToHashSet(StringComparer.Ordinal);
        return Identity("tuning-contract/2:", sources.Where(pair => !otherRoles.Contains(pair.Key)));
    }

    private static string Identity(string prefix, IEnumerable<KeyValuePair<string, string>> sources) => prefix + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sources.OrderBy(p => p.Key, StringComparer.Ordinal), TuningInputJson.Options))));

    private static TuningContractSnapshot Read()
    {
        using var stream = typeof(TuningContractIdentity).Assembly.GetManifestResourceStream("FamilyLibrarian.ModelTuning.contracts.json")
            ?? throw new InvalidOperationException("Missing contract identity.");
        var snapshot = JsonSerializer.Deserialize<TuningContractSnapshot>(stream, TuningInputJson.Options)
            ?? throw new InvalidOperationException("Missing contract snapshot.");
        if (snapshot.SchemaVersion != 1 || snapshot.Sources.Count == 0) throw new InvalidOperationException("Invalid contract identity.");
        return snapshot;
    }
}
