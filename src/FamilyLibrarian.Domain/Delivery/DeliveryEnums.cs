namespace FamilyLibrarian.Domain.Delivery;

/// <summary>
/// The mechanism used to get an owned book to a user's configured
/// <see cref="DeliveryTarget"/>. Only one exists today; see
/// docs/01-product-architecture-spec.md §15/§15.1 for the intended future
/// set (e.g. a direct-device or browser-download method).
/// </summary>
public enum DeliveryTargetProvider
{
    CwaKindleEmail = 1
}

/// <summary>The lifecycle of one <see cref="DeliveryAttempt"/> row.</summary>
public enum DeliveryAttemptStatus
{
    Pending = 1,
    Submitting = 2,
    Submitted = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// Whether the user has confirmed a <see cref="DeliveryAttemptStatus.Submitted"/>
/// attempt actually arrived on their Kindle (KINDLE-7). Distinct from
/// <see cref="DeliveryAttemptStatus"/>: CWA accepting the send only proves
/// submission, never on-device receipt, so this tracks a separate,
/// user-reported fact layered on top of a terminal <c>Submitted</c> row
/// rather than a further status transition.
/// </summary>
public enum DeliveryConfirmationStatus
{
    Unconfirmed = 1,
    Confirmed = 2,
    ReportedMissing = 3
}
