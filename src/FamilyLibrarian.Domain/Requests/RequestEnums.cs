namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// The deliberately small initial request lifecycle.
/// </summary>
/// <remarks>
/// Acquisition, security review, approval, and delivery states arrive with the
/// work that implements them. <see cref="RequestStatusHistory"/> and the
/// per-format children exist so that expansion does not require a rewrite.
/// </remarks>
public enum RequestStatus
{
    PendingAcquisition = 1,
    NeedsReview = 2,
    NotAvailable = 3,
    Cancelled = 4
}

public enum RequestMediaType
{
    Ebook = 1,
    Audiobook = 2
}

/// <summary>
/// Per-format state. It tracks the parent request in this slice, but exists as
/// its own column because acquisition eventually resolves one format at a time.
/// </summary>
public enum RequestFormatStatus
{
    Requested = 1,
    NotAvailable = 2,
    Cancelled = 3
}
