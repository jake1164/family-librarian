namespace FamilyLibrarian.Domain.Delivery;

/// <summary>
/// One attempt to send an already-catalogued ebook to a user's
/// <see cref="DeliveryTarget"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Acquisition.AcquisitionJob"/>'s shape: private setters,
/// <see cref="TransitionTo"/>, one row per attempt. A request/user pair can
/// accumulate more than one row over time -- a retry after
/// <see cref="DeliveryAttemptStatus.Failed"/> creates a new row with
/// <see cref="AttemptNumber"/> incremented rather than reopening this one, the
/// same "failed attempt, then a fresh one" pattern <c>AcquisitionJob</c> uses --
/// so KINDLE-7's "didn't receive it" history is never lost to an overwrite.
/// <para>
/// <see cref="RequestId"/> is nullable: the existing-book "already in CWA -&gt;
/// Send to Kindle" fast path creates an attempt with no <c>BookRequest</c>
/// involved at all -- see the kindle delivery beta plan's "Shape
/// reconciliation" addendum.
/// </para>
/// </remarks>
public sealed class DeliveryAttempt
{
    private DeliveryAttempt()
    {
    }

    public DeliveryAttempt(
        Guid? requestId,
        Guid userId,
        Guid deliveryTargetId,
        string provider,
        string externalBookId,
        string bookFormat,
        bool convert,
        int attemptNumber,
        DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        if (deliveryTargetId == Guid.Empty)
        {
            throw new ArgumentException("A delivery target ID is required.", nameof(deliveryTargetId));
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("A provider id is required.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(externalBookId))
        {
            throw new ArgumentException("An external book ID is required.", nameof(externalBookId));
        }

        if (string.IsNullOrWhiteSpace(bookFormat))
        {
            throw new ArgumentException("A book format is required.", nameof(bookFormat));
        }

        if (attemptNumber < 1)
        {
            throw new ArgumentException("The attempt number must be at least 1.", nameof(attemptNumber));
        }

        Id = Guid.NewGuid();
        RequestId = requestId;
        UserId = userId;
        DeliveryTargetId = deliveryTargetId;
        Provider = provider.Trim();
        ExternalBookId = externalBookId.Trim();
        BookFormat = bookFormat.Trim();
        Convert = convert;
        AttemptNumber = attemptNumber;
        Status = DeliveryAttemptStatusTransitions.InitialStatus;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid? RequestId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid DeliveryTargetId { get; private set; }

    public string Provider { get; private set; } = null!;

    public string ExternalBookId { get; private set; } = null!;

    public string BookFormat { get; private set; } = null!;

    public bool Convert { get; private set; }

    public int AttemptNumber { get; private set; }

    public DeliveryAttemptStatus Status { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Only meaningful when <see cref="Status"/> is <see cref="DeliveryAttemptStatus.Failed"/>.</summary>
    public bool IsRetryable { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    /// <exception cref="InvalidDeliveryAttemptTransitionException">
    /// The move is not in <see cref="DeliveryAttemptStatusTransitions"/>.
    /// </exception>
    public void TransitionTo(
        DeliveryAttemptStatus to, DateTimeOffset atUtc, string? failureReason = null, bool retryable = false)
    {
        if (!DeliveryAttemptStatusTransitions.IsAllowed(Status, to))
        {
            throw new InvalidDeliveryAttemptTransitionException(Status, to);
        }

        StartedAtUtc ??= atUtc;
        Status = to;
        FailureReason = to == DeliveryAttemptStatus.Failed
            ? (string.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim())
            : null;
        IsRetryable = to == DeliveryAttemptStatus.Failed && retryable;

        if (to is DeliveryAttemptStatus.Submitted or DeliveryAttemptStatus.Failed or DeliveryAttemptStatus.Cancelled)
        {
            CompletedAtUtc = atUtc;
        }
    }
}
