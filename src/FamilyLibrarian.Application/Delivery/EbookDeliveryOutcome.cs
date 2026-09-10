namespace FamilyLibrarian.Application.Delivery;

/// <summary>
/// Distinct from <see cref="Publishing.ConnectionTestOutcome"/> because a real
/// delivery outcome distinguishes safe retries from uncertain submissions.
/// </summary>
public enum EbookDeliveryStatus
{
    Delivered,

    /// <summary>No usable configuration saved -- an admin action, not a retry.</summary>
    NotConfigured,

    /// <summary>
    /// The destination understood the request and declined it (e.g. its own
    /// outbound mail isn't set up, or the format/convert combination isn't
    /// sendable) -- a same-input retry will not help without a config or
    /// admin fix.
    /// </summary>
    Rejected,

    /// <summary>A failure before the send was dispatched; safe to retry later.</summary>
    TransportFailure,

    /// <summary>The send may have reached the provider. Never retry automatically.</summary>
    SubmissionUnknown
}

public sealed record EbookDeliveryOutcome(EbookDeliveryStatus Status, string Message)
{
    public bool Succeeded => Status == EbookDeliveryStatus.Delivered;

    public static EbookDeliveryOutcome Unknown(string message) => new(EbookDeliveryStatus.SubmissionUnknown, message);

    public static EbookDeliveryOutcome Delivered(string message) => new(EbookDeliveryStatus.Delivered, message);

    public static EbookDeliveryOutcome NotConfigured(string message) => new(EbookDeliveryStatus.NotConfigured, message);

    public static EbookDeliveryOutcome Rejected(string message) => new(EbookDeliveryStatus.Rejected, message);

    public static EbookDeliveryOutcome TransportFailure(string message) => new(EbookDeliveryStatus.TransportFailure, message);
}
