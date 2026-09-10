using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

public sealed record PersonalDeliveryAttemptView(
    Guid Id, Guid DeliveryId, Guid? RequestId, string? BookTitle,
    DeliveryAttemptStatus Status, DeliveryConfirmationStatus ConfirmationStatus,
    int AttemptNumber, string? FailureReason, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset? ConfirmedAtUtc, Guid LatestAttemptId,
    bool CanRetry, DateTimeOffset? NextAutomaticRetryAtUtc, bool AutomaticRetriesExhausted)
{
    public static PersonalDeliveryAttemptView From(DeliveryAttempt attempt, Guid latestId) => new(
        attempt.Id, attempt.DeliveryId, attempt.RequestId, attempt.BookTitle,
        attempt.Status, attempt.ConfirmationStatus, attempt.AttemptNumber, attempt.FailureReason,
        attempt.CreatedAtUtc, attempt.CompletedAtUtc, attempt.ConfirmedAtUtc, latestId,
        latestId == attempt.Id && DeliveryRetryPolicy.CanRetry(attempt.Status, attempt.ConfirmationStatus),
        DeliveryRetryPolicy.NextAutomaticRetryAt(attempt.Status, attempt.IsRetryable, attempt.AttemptNumber,
            attempt.CompletedAtUtc, latestId == attempt.Id),
        latestId == attempt.Id && attempt.Status == DeliveryAttemptStatus.Failed && attempt.IsRetryable &&
            attempt.AttemptNumber >= DeliveryRetryPolicy.MaxAttempts);
}
