using System.Text;

namespace FamilyLibrarian.Domain.Evidence;

/// <summary>
/// The distinct questions artifact evidence answers. Deliberately separate so no single
/// "verified" flag can stand in for several different facts (alpha.6 D-14).
/// </summary>
public enum EvidenceDimension
{
    /// <summary>Who or what vouched for the identity before the file was read (source, built-in provider, uploader).</summary>
    IdentityAttestation = 1,

    /// <summary>Whether the file's own content independently supports, contradicts, or could not speak to its identity.</summary>
    IdentityCorroboration = 2,

    /// <summary>What the file itself declares about its language, compared with what was accepted.</summary>
    Language = 3,

    /// <summary>Whether container and format validators reported a structural defect.</summary>
    Structure = 4,

    /// <summary>Whether the file appears to be the complete work. Not assessed until a later slice.</summary>
    Completeness = 5,

    /// <summary>Malware and safety scanning results.</summary>
    Security = 6
}

/// <summary>
/// One persisted, citable observation about a staged <c>MediaAsset</c>.
/// </summary>
/// <remarks>
/// Evidence is append-only history: reprocessing an asset adds a new record (new
/// <see cref="RecordId"/>) and never edits or removes an earlier one. It records
/// <em>what was observed</em> and by whom; it never carries an accept or reject
/// decision, which stays with the existing security and approval services.
/// <para>
/// Only bounded, non-content values are stored: a short code or verdict, a scanner
/// or tool version, a language tag, a validator's short message. Book text, audio,
/// long excerpts and chat transcripts are never persisted here
/// (<see cref="MaxValueLength"/> enforces the bound).
/// </para>
/// </remarks>
public sealed class ArtifactEvidence
{
    public const int SchemaVersion = 1;
    public const int MaxValueLength = 256;
    public const int MaxFindingIdLength = 96;
    public const int MaxCodeLength = 96;
    public const int MaxSourceLength = 128;
    public const int MaxToolVersionLength = 64;
    public const int MaxRecordKindLength = 48;

    private ArtifactEvidence()
    {
    }

    public ArtifactEvidence(
        Guid mediaAssetId,
        string sha256,
        string recordKind,
        Guid recordId,
        string findingId,
        EvidenceDimension dimension,
        string code,
        EvidencePolarity polarity,
        string source,
        string? value,
        string? toolVersion,
        DateTimeOffset recordedAtUtc)
    {
        if (mediaAssetId == Guid.Empty) throw new ArgumentException("A media asset id is required.", nameof(mediaAssetId));
        if (recordId == Guid.Empty) throw new ArgumentException("A record id is required.", nameof(recordId));
        if (!Enum.IsDefined(dimension)) throw new ArgumentOutOfRangeException(nameof(dimension));
        if (!Enum.IsDefined(polarity)) throw new ArgumentOutOfRangeException(nameof(polarity));
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (recordKind.Length > MaxRecordKindLength || findingId.Length > MaxFindingIdLength ||
            code.Length > MaxCodeLength || source.Length > MaxSourceLength)
            throw new ArgumentException("An evidence identifier exceeds its stored length.");

        Id = Guid.NewGuid();
        MediaAssetId = mediaAssetId;
        Sha256 = sha256.Trim();
        RecordKind = recordKind;
        RecordId = recordId;
        FindingId = findingId;
        Dimension = dimension;
        Code = code;
        Polarity = polarity;
        Source = source;
        Value = Bound(value);
        ToolVersion = Bound(toolVersion, MaxToolVersionLength);
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid MediaAssetId { get; private set; }

    /// <summary>The artifact hash at observation time, so evidence stays tied to the exact bytes observed.</summary>
    public string Sha256 { get; private set; } = null!;

    /// <summary>Persisted evidence-schema version (<see cref="SchemaVersion"/> when written).</summary>
    public int EvidenceSchemaVersion { get; private set; } = SchemaVersion;

    /// <summary>The kind of pass that produced the record (for example <c>security-evaluation</c>, <c>identity-verification</c>).</summary>
    public string RecordKind { get; private set; } = null!;

    /// <summary>Identifies one pass. Together with <see cref="FindingId"/> it cites exactly one finding.</summary>
    public Guid RecordId { get; private set; }

    /// <summary>Unique within one <see cref="RecordId"/>.</summary>
    public string FindingId { get; private set; } = null!;

    public EvidenceDimension Dimension { get; private set; }

    /// <summary>A stable <see cref="EvidenceReasonCodes"/> value stored as text.</summary>
    public string Code { get; private set; } = null!;

    public EvidencePolarity Polarity { get; private set; }

    /// <summary>The component that observed it (scanner, validator or verifier id, or a pipeline stage).</summary>
    public string Source { get; private set; } = null!;

    /// <summary>A bounded, non-content detail. Never book text, audio or an excerpt.</summary>
    public string? Value { get; private set; }

    public string? ToolVersion { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    /// <summary>Trims, strips control characters (hostile file-derived text), and bounds a stored value.</summary>
    public static string? Bound(string? value, int maxLength = MaxValueLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value.Trim())
        {
            if (builder.Length >= maxLength) break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var bounded = builder.ToString().Trim();
        return bounded.Length == 0 ? null : bounded;
    }
}

/// <summary>Names of the evidence passes persisted in <see cref="ArtifactEvidence.RecordKind"/>.</summary>
public static class ArtifactEvidenceRecordKinds
{
    public const string SecurityEvaluation = "security-evaluation";
    public const string IdentityVerification = "identity-verification";
}
