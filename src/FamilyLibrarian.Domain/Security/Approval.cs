namespace FamilyLibrarian.Domain.Security;

/// <summary>One recorded approve/reject decision against a <see cref="SecurityEvaluation"/>.</summary>
public sealed class Approval
{
    public const int MaxReasonLength = 1_000;

    private Approval()
    {
    }

    internal Approval(
        Guid securityEvaluationId,
        ApprovalDecision decision,
        ApprovalActorType actorType,
        Guid? actorUserId,
        string? policyName,
        string? reason,
        DateTimeOffset decidedAtUtc)
    {
        Id = Guid.NewGuid();
        SecurityEvaluationId = securityEvaluationId;
        Decision = decision;
        ActorType = actorType;
        ActorUserId = actorUserId;
        PolicyName = string.IsNullOrWhiteSpace(policyName) ? null : policyName.Trim();
        Reason = CleanReason(reason);
        DecidedAtUtc = decidedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid SecurityEvaluationId { get; private set; }

    public ApprovalDecision Decision { get; private set; }

    public ApprovalActorType ActorType { get; private set; }

    /// <summary>The acting administrator, or <see langword="null"/> for a policy/system decision.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>The named policy profile that made the decision, when <see cref="ActorType"/> is <see cref="ApprovalActorType.Policy"/>.</summary>
    public string? PolicyName { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset DecidedAtUtc { get; private set; }

    private static string? CleanReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxReasonLength)
        {
            throw new ArgumentException(
                $"The reason may not exceed {MaxReasonLength} characters.",
                nameof(value));
        }

        return trimmed;
    }
}
