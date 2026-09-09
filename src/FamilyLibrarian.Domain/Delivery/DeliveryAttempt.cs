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
        DateTimeOffset createdAtUtc,
        string? bookTitle = null,
        Guid? deliveryId = null)
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

        if (deliveryId == Guid.Empty)
            throw new ArgumentException("A delivery identity must not be empty.", nameof(deliveryId));

        Id = Guid.NewGuid();
        DeliveryId = deliveryId ?? Id;
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
        BookTitle = string.IsNullOrWhiteSpace(bookTitle) ? null : bookTitle.Trim();
        ConfirmationStatus = DeliveryConfirmationStatus.Unconfirmed;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Stable identity shared by all attempts for one logical delivery.</summary>
    public Guid DeliveryId { get; private set; }

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

    /// <summary>
    /// Denormalized purely for display (notifications, admin visibility) --
    /// captured once at creation and carried forward unchanged by a retry
    /// row, not re-resolved from the Work each time. Null when the caller had
    /// no title cheaply at hand.
    /// </summary>
    public string? BookTitle { get; private set; }

    /// <summary>KINDLE-7: only meaningful once <see cref="Status"/> is <see cref="DeliveryAttemptStatus.Submitted"/>.</summary>
    public DeliveryConfirmationStatus ConfirmationStatus { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

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
        FailureReason = to is DeliveryAttemptStatus.Failed or DeliveryAttemptStatus.SubmissionUnknown or DeliveryAttemptStatus.Cancelled
            ? (string.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim())
            : null;
        IsRetryable = to == DeliveryAttemptStatus.Failed && retryable;

        if (to is DeliveryAttemptStatus.Submitted or DeliveryAttemptStatus.Failed or DeliveryAttemptStatus.Cancelled or DeliveryAttemptStatus.SubmissionUnknown)
        {
            CompletedAtUtc = atUtc;
        }
    }

    /// <summary>
    /// The user confirms this submitted delivery actually arrived on their
    /// Kindle. Re-confirming, or switching from a prior <see cref="ReportMissing"/>,
    /// is allowed -- this tracks the user's current answer, not a one-shot event.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Status"/> is not <see cref="DeliveryAttemptStatus.Submitted"/>.
    /// </exception>
    public void ConfirmReceived(DateTimeOffset atUtc)
    {
        if (Status != DeliveryAttemptStatus.Submitted)
        {
            throw new InvalidOperationException(
                "Only a submitted delivery attempt can be confirmed received.");
        }

        ConfirmationStatus = DeliveryConfirmationStatus.Confirmed;
        ConfirmedAtUtc = atUtc;
    }

    /// <summary>
    /// The user reports that a submitted delivery never arrived. This does not
    /// reopen <see cref="Status"/> -- CWA genuinely accepted the send, so
    /// <see cref="DeliveryAttemptStatus.Submitted"/> remains accurate; a retry
    /// creates a new row instead, the same "failed attempt, then a fresh one"
    /// pattern as a submission failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Status"/> is not <see cref="DeliveryAttemptStatus.Submitted"/>.
    /// </exception>
    public void ReportMissing(DateTimeOffset atUtc)
    {
        if (Status != DeliveryAttemptStatus.Submitted)
        {
            throw new InvalidOperationException(
                "Only a submitted delivery attempt can be reported missing.");
        }

        ConfirmationStatus = DeliveryConfirmationStatus.ReportedMissing;
        ConfirmedAtUtc = atUtc;
    }
}
