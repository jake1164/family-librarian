namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// One entry from a completed <see cref="ProviderAcquisitionJob"/>'s
/// <c>GET /acquire/{jobId}/outputs</c> listing (protocol v2 §8a) — a job may
/// report more than one (an ebook plus a cover, the tracks of an audiobook,
/// or a single torrent/NZB descriptor).
/// </summary>
public sealed class ProviderAcquisitionJobOutput
{
    private ProviderAcquisitionJobOutput()
    {
    }

    internal ProviderAcquisitionJobOutput(
        Guid providerAcquisitionJobId,
        string outputId,
        ProviderOutputKind kind,
        string? role,
        string? filename,
        string? contentType,
        long? sizeBytes,
        string? uri,
        string? uriScheme,
        string? checksumsJson,
        DateTimeOffset? retentionExpiresAtUtc,
        DateTimeOffset createdAtUtc)
    {
        if (providerAcquisitionJobId == Guid.Empty)
        {
            throw new ArgumentException("A provider acquisition job ID is required.", nameof(providerAcquisitionJobId));
        }

        if (string.IsNullOrWhiteSpace(outputId))
        {
            throw new ArgumentException("An output id is required.", nameof(outputId));
        }

        if (kind == ProviderOutputKind.Uri && string.IsNullOrWhiteSpace(uri))
        {
            throw new ArgumentException("A uri-kind output requires a uri.", nameof(uri));
        }

        Id = Guid.NewGuid();
        ProviderAcquisitionJobId = providerAcquisitionJobId;
        OutputId = outputId.Trim();
        Kind = kind;
        Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        Filename = filename;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Uri = uri;
        UriScheme = uriScheme;
        ChecksumsJson = checksumsJson;
        RetentionExpiresAtUtc = retentionExpiresAtUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid ProviderAcquisitionJobId { get; private set; }

    public ProviderAcquisitionJob ProviderAcquisitionJob { get; private set; } = null!;

    /// <summary>The provider's own id for this output, used to fetch it via <c>GET .../outputs/{outputId}</c>.</summary>
    public string OutputId { get; private set; } = null!;

    public ProviderOutputKind Kind { get; private set; }

    /// <summary>Open string — <c>primary</c>/<c>ebook</c>/<c>audio-part</c>/<c>cover</c>/etc., or anything else meaningful.</summary>
    public string? Role { get; private set; }

    public string? Filename { get; private set; }

    public string? ContentType { get; private set; }

    public long? SizeBytes { get; private set; }

    /// <summary>The actual URI for a <see cref="ProviderOutputKind.Uri"/> output — nothing to fetch bytes for.</summary>
    public string? Uri { get; private set; }

    public string? UriScheme { get; private set; }

    /// <summary>Extensible <c>{algorithm, value}</c> pairs, opaque JSON — a cross-check, never a substitute for Family Librarian's own computed checksum.</summary>
    public string? ChecksumsJson { get; private set; }

    public DateTimeOffset? RetentionExpiresAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
