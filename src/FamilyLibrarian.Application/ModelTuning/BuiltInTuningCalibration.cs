using System.Text.Json;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTuning;

public enum TuningRoleAssessability { Frozen, NotAssessable, NotScorable }
public sealed record TuningRoleCalibrationEntry(ModelRole Role, TuningRoleAssessability Status,
    FrozenRoleCalibration? Calibration, string? Reason);
public sealed record TuningCalibrationManifest(int SchemaVersion, string CorpusVersion, string LabelVersion,
    IReadOnlyList<TuningRoleCalibrationEntry> Roles);

/// <summary>Reviewed, identity-pinned headless calibration. Not a production role setting.</summary>
public static class BuiltInTuningCalibration
{
    public static TuningCalibrationManifest LoadEmbedded(BuiltInTuningCorpus corpus)
    {
        using var stream = typeof(BuiltInTuningCalibration).Assembly.GetManifestResourceStream("FamilyLibrarian.ModelTuning.calibration.json")
            ?? throw new InvalidOperationException("Missing calibration resource.");
        using var reader = new StreamReader(stream);
        return Load(corpus, reader.ReadToEnd());
    }

    public static TuningCalibrationManifest Load(BuiltInTuningCorpus corpus, string json)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        using var document = JsonDocument.Parse(json);
        TuningInputJson.CheckTree(document.RootElement);
        var manifest = document.RootElement.Deserialize<TuningCalibrationManifest>(TuningInputJson.Options)
            ?? throw new JsonException("Missing calibration manifest.");
        if (manifest.SchemaVersion != 1 || manifest.CorpusVersion != corpus.Manifest.CorpusVersion || manifest.LabelVersion != corpus.Manifest.LabelVersion ||
            !manifest.Roles.Select(r => r.Role).Order().SequenceEqual(TuningRoles.Scored.Order()))
            throw new JsonException("Calibration scope or corpus identity differs.");
        foreach (var entry in manifest.Roles)
        {
            if (!Enum.IsDefined(entry.Status)) throw new JsonException("Unknown assessability.");
            if (entry.Status != TuningRoleAssessability.Frozen)
            {
                if (entry.Calibration is not null || string.IsNullOrWhiteSpace(entry.Reason))
                    throw new JsonException("Unassessable roles require a reason and no calibration.");
                continue;
            }
            if (entry.Calibration is not { IsFrozen: true } calibration || entry.Reason is not null)
                throw new JsonException("Frozen role requires an identity-pinned calibration.");
            var cases = corpus.Cases.Where(c => c.Case.Role == entry.Role).Select(c => c.Case).ToArray();
            TuningAdequacyRules.ValidateCalibration(calibration, corpus.CalibrationKey(entry.Role), TuningScoring.AbstainBaseline(cases));
        }
        return manifest;
    }
}
