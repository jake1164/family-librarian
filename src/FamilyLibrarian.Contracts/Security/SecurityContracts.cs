namespace FamilyLibrarian.Contracts.Security;

public sealed record SecurityEvaluationResponse(
    Guid EvaluationId,
    Guid AssetId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ApprovalDecisionRequest(string? Reason);

/// <summary>The full detail of one security evaluation, used by the admin review queue.</summary>
public sealed record SecurityEvaluationDetailResponse(
    Guid EvaluationId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<SecurityScanResultResponse> ScanResults,
    IReadOnlyList<FormatValidationResultResponse> ValidationResults,
    IReadOnlyList<SecurityApprovalResponse> Approvals);

public sealed record SecurityScanResultResponse(
    string ScannerId,
    bool IsRequired,
    string Status,
    string? ThreatName,
    DateTimeOffset ScannedAtUtc);

public sealed record FormatValidationResultResponse(
    string ValidatorId,
    bool IsValid,
    string? Message,
    DateTimeOffset ValidatedAtUtc);

public sealed record SecurityApprovalResponse(
    string Decision,
    string ActorType,
    string? Reason,
    DateTimeOffset DecidedAtUtc);
