namespace FamilyLibrarian.Domain.Security;

/// <summary>
/// One security-pipeline pass over one <c>MediaAsset</c>: every scanner and
/// format-validator result gathered for it, and the approve/reject decisions
/// made against it.
/// </summary>
/// <remarks>
/// <see cref="Evaluate"/> is where the fail-closed policy actually lives: a
/// required scanner reporting <see cref="ScanResultStatus.Error"/> or
/// <see cref="ScanResultStatus.Unavailable"/> can never resolve to
/// <see cref="SecurityEvaluationStatus.Passed"/> — it resolves to
/// <see cref="SecurityEvaluationStatus.ReviewRequired"/> instead, which only an
/// administrator can override. A confirmed <see cref="ScanResultStatus.Detected"/>
/// or an invalid format resolves to <see cref="SecurityEvaluationStatus.Failed"/>,
/// which nothing can override — see <see cref="Approve"/>.
/// </remarks>
public sealed class SecurityEvaluation
{
    private readonly List<SecurityScanResult> _scanResults = [];
    private readonly List<FormatValidationResult> _validationResults = [];
    private readonly List<Approval> _approvals = [];

    private SecurityEvaluation()
    {
    }

    public SecurityEvaluation(Guid assetId, string policyVersion, DateTimeOffset createdAtUtc)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("An asset ID is required.", nameof(assetId));
        }

        if (string.IsNullOrWhiteSpace(policyVersion))
        {
            throw new ArgumentException("A policy version is required.", nameof(policyVersion));
        }

        Id = Guid.NewGuid();
        AssetId = assetId;
        PolicyVersion = policyVersion.Trim();
        Status = SecurityEvaluationStatus.Pending;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid AssetId { get; private set; }

    public string PolicyVersion { get; private set; } = null!;

    public SecurityEvaluationStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public IReadOnlyCollection<SecurityScanResult> ScanResults => _scanResults;

    public IReadOnlyCollection<FormatValidationResult> ValidationResults => _validationResults;

    public IReadOnlyCollection<Approval> Approvals => _approvals;

    public SecurityScanResult RecordScanResult(
        string scannerId,
        bool isRequired,
        ScanResultStatus status,
        string? threatName,
        string? scannerVersion,
        DateTimeOffset atUtc)
    {
        if (Status != SecurityEvaluationStatus.Pending)
        {
            throw new InvalidOperationException("This evaluation has already been evaluated.");
        }

        var result = new SecurityScanResult(Id, scannerId, isRequired, status, threatName, scannerVersion, atUtc);
        _scanResults.Add(result);
        return result;
    }

    public FormatValidationResult RecordValidationResult(
        string validatorId,
        bool isValid,
        string? message,
        DateTimeOffset atUtc)
    {
        if (Status != SecurityEvaluationStatus.Pending)
        {
            throw new InvalidOperationException("This evaluation has already been evaluated.");
        }

        var result = new FormatValidationResult(Id, validatorId, isValid, message, atUtc);
        _validationResults.Add(result);
        return result;
    }

    /// <summary>
    /// Resolves <see cref="Status"/> from every result recorded so far. Fail-closed:
    /// see the type-level remarks.
    /// </summary>
    public void Evaluate(DateTimeOffset atUtc)
    {
        if (Status != SecurityEvaluationStatus.Pending)
        {
            throw new InvalidOperationException("This evaluation has already been evaluated.");
        }

        var requiredScans = _scanResults.Where(result => result.IsRequired).ToArray();
        var anyThreatDetected = requiredScans.Any(result => result.Status == ScanResultStatus.Detected);
        var anyInconclusive = requiredScans.Any(result =>
            result.Status is ScanResultStatus.Error or ScanResultStatus.Unavailable);
        var anyInvalidFormat = _validationResults.Any(result => !result.IsValid);

        Status = anyThreatDetected || anyInvalidFormat
            ? SecurityEvaluationStatus.Failed
            : anyInconclusive
                ? SecurityEvaluationStatus.ReviewRequired
                : SecurityEvaluationStatus.Passed;

        CompletedAtUtc = atUtc;
    }

    /// <exception cref="InvalidOperationException">
    /// The evaluation is <see cref="SecurityEvaluationStatus.Failed"/> (never
    /// overridable), still <see cref="SecurityEvaluationStatus.Pending"/>, or
    /// <see cref="SecurityEvaluationStatus.ReviewRequired"/> and the caller is
    /// not an administrator.
    /// </exception>
    public Approval Approve(
        ApprovalActorType actorType,
        Guid? actorUserId,
        string? policyName,
        string? reason,
        DateTimeOffset atUtc)
    {
        switch (Status)
        {
            case SecurityEvaluationStatus.Pending:
                throw new InvalidOperationException("This evaluation has not completed yet.");
            case SecurityEvaluationStatus.Failed:
                throw new InvalidOperationException(
                    "A failed security evaluation cannot be approved by anyone.");
            case SecurityEvaluationStatus.ReviewRequired when actorType != ApprovalActorType.Admin:
                throw new InvalidOperationException(
                    "Only an administrator may approve a review-required evaluation.");
        }

        var approval = new Approval(Id, ApprovalDecision.Approved, actorType, actorUserId, policyName, reason, atUtc);
        _approvals.Add(approval);
        return approval;
    }

    public Approval Reject(ApprovalActorType actorType, Guid? actorUserId, string? reason, DateTimeOffset atUtc)
    {
        var approval = new Approval(Id, ApprovalDecision.Rejected, actorType, actorUserId, null, reason, atUtc);
        _approvals.Add(approval);
        return approval;
    }
}
