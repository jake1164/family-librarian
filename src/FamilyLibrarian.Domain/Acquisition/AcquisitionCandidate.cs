namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// One file a provider (or a manual operator) offered for an
/// <see cref="AcquisitionJob"/>.
/// </summary>
public sealed class AcquisitionCandidate
{
    private AcquisitionCandidate()
    {
    }

    internal AcquisitionCandidate(
        Guid acquisitionJobId,
        string providerId,
        string providerReference,
        string? title,
        string? author,
        string? format,
        long? sizeBytes,
        TimeSpan? duration,
        int? bitrateKbps,
        string? metadataJson,
        double? confidenceScore,
        DateTimeOffset createdAtUtc)
    {
        if (acquisitionJobId == Guid.Empty)
        {
            throw new ArgumentException("An acquisition job ID is required.", nameof(acquisitionJobId));
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        }

        if (string.IsNullOrWhiteSpace(providerReference))
        {
            throw new ArgumentException("A provider reference is required.", nameof(providerReference));
        }

        Id = Guid.NewGuid();
        AcquisitionJobId = acquisitionJobId;
        ProviderId = providerId.Trim();
        ProviderReference = providerReference.Trim();
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim();
        SizeBytes = sizeBytes;
        Duration = duration;
        BitrateKbps = bitrateKbps;
        MetadataJson = metadataJson;
        ConfidenceScore = confidenceScore;
        Status = AcquisitionCandidateStatus.Discovered;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid AcquisitionJobId { get; private set; }

    public AcquisitionJob AcquisitionJob { get; private set; } = null!;

    public string ProviderId { get; private set; } = null!;

    /// <summary>The provider's own identifier for this candidate (opaque to core code).</summary>
    public string ProviderReference { get; private set; } = null!;

    public string? Title { get; private set; }

    public string? Author { get; private set; }

    public string? Format { get; private set; }

    public long? SizeBytes { get; private set; }

    public TimeSpan? Duration { get; private set; }

    public int? BitrateKbps { get; private set; }

    /// <summary>Opaque provider-reported metadata, retained for review, never parsed as trusted fact.</summary>
    public string? MetadataJson { get; private set; }

    public double? ConfidenceScore { get; private set; }

    public AcquisitionCandidateStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    internal void SetStatus(AcquisitionCandidateStatus status, DateTimeOffset atUtc)
    {
        Status = status;
        UpdatedAtUtc = atUtc;
    }
}
