using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

public static class DeliveryRetryPolicy
{
    public const int MaxAttempts = 3;

    public static TimeSpan Cooldown(int attemptNumber) =>
        attemptNumber <= 1 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10);

    public static bool CanRetry(DeliveryAttemptStatus status, DeliveryConfirmationStatus confirmation) =>
        status is DeliveryAttemptStatus.Failed or DeliveryAttemptStatus.SubmissionUnknown ||
        (status == DeliveryAttemptStatus.Submitted && confirmation == DeliveryConfirmationStatus.ReportedMissing);

    public static DateTimeOffset? NextAutomaticRetryAt(DeliveryAttemptStatus status, bool retryable,
        int attemptNumber, DateTimeOffset? completedAt, bool latest) =>
        latest && status == DeliveryAttemptStatus.Failed && retryable && attemptNumber < MaxAttempts && completedAt is { } completed
            ? completed + Cooldown(attemptNumber) : null;
}
