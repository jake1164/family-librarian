using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyLibrarian.Application.ModelTuning;

public sealed record TuningContractSnapshot(int SchemaVersion, IReadOnlyDictionary<string, string> Sources);

/// <summary>Build-time contract fingerprint checked against source by tests; no production filesystem access.</summary>
public static class TuningContractIdentity
{
    public static TuningContractSnapshot Snapshot { get; } = Read();
    public static string Current { get; } = "tuning-contract/1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Snapshot.Sources.OrderBy(p => p.Key, StringComparer.Ordinal), TuningInputJson.Options))));

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
