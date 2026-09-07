namespace FamilyLibrarian.Application.Delivery;

/// <summary>
/// Distinct from <see cref="Publishing.ConnectionTestOutcome"/> because a real
/// delivery failure carries a retryability signal a future retry sweep
/// (KINDLE-5) needs, which a plain pass/fail does not.
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

    /// <summary>A network/timeout/unexpected-response failure -- worth retrying later.</summary>
    TransportFailure
}

public sealed record EbookDeliveryOutcome(EbookDeliveryStatus Status, string Message)
{
    public bool Succeeded => Status == EbookDeliveryStatus.Delivered;

    public static EbookDeliveryOutcome Delivered(string message) => new(EbookDeliveryStatus.Delivered, message);

    public static EbookDeliveryOutcome NotConfigured(string message) => new(EbookDeliveryStatus.NotConfigured, message);

    public static EbookDeliveryOutcome Rejected(string message) => new(EbookDeliveryStatus.Rejected, message);

    public static EbookDeliveryOutcome TransportFailure(string message) => new(EbookDeliveryStatus.TransportFailure, message);
}
