namespace FamilyLibrarian.Contracts.Security;

public sealed record SecurityEvaluationResponse(
    Guid EvaluationId,
    Guid AssetId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ApprovalDecisionRequest(string? Reason);
