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
    Cancelled = 4,
    Available = 5
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
    Cancelled = 3,
    Available = 4
}

/// <summary>
/// Why a request is <see cref="RequestStatus.NeedsReview"/> -- see
/// .ai_docs/family-librarian-accuracy-selfservice-alpha2-plan.md (SELFSERV-1).
/// </summary>
public enum RequestReviewCategory
{
    /// <summary>
    /// Multiple plausible candidates for the same work, with no safety or
    /// trust concern -- just no clear single winner (e.g. two English
    /// editions, or a same-title/author candidate excluded for language).
    /// This is a preference decision the requester is positioned to make, so
    /// it also notifies them and offers an inline pick, unlike the other two
    /// categories below.
    /// </summary>
    PreferenceAmbiguity = 1,

    /// <summary>Different providers confidently disagree on the file -- admin-only, unchanged from before this enum existed.</summary>
    ProviderDisagreement = 2,

    /// <summary>A downloaded file failed its post-download security/identity check -- admin-only, unchanged from before this enum existed.</summary>
    SecurityOrIdentityFailure = 3
}
