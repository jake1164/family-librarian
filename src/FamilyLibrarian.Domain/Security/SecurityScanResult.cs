namespace FamilyLibrarian.Domain.Security;

/// <summary>One scanner's result against one <see cref="SecurityEvaluation"/>.</summary>
public sealed class SecurityScanResult
{
    private SecurityScanResult()
    {
    }

    internal SecurityScanResult(
        Guid securityEvaluationId,
        string scannerId,
        bool isRequired,
        ScanResultStatus status,
        string? threatName,
        string? scannerVersion,
        DateTimeOffset scannedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(scannerId))
        {
            throw new ArgumentException("A scanner id is required.", nameof(scannerId));
        }

        Id = Guid.NewGuid();
        SecurityEvaluationId = securityEvaluationId;
        ScannerId = scannerId.Trim();
        IsRequired = isRequired;
        Status = status;
        ThreatName = string.IsNullOrWhiteSpace(threatName) ? null : threatName.Trim();
        ScannerVersion = string.IsNullOrWhiteSpace(scannerVersion) ? null : scannerVersion.Trim();
        ScannedAtUtc = scannedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid SecurityEvaluationId { get; private set; }

    public string ScannerId { get; private set; } = null!;

    public bool IsRequired { get; private set; }

    public ScanResultStatus Status { get; private set; }

    /// <summary>The detected threat's signature name, only when <see cref="Status"/> is <see cref="ScanResultStatus.Detected"/>.</summary>
    public string? ThreatName { get; private set; }

    public string? ScannerVersion { get; private set; }

    public DateTimeOffset ScannedAtUtc { get; private set; }
}
