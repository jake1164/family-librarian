using MudBlazor;

namespace FamilyLibrarian.Web.Client.Theme;

/// <summary>
/// Single source of truth for how request/format status and Ebook/Audiobook
/// media type are drawn wherever they appear as a chip. Chip color always
/// means status (grey = inactive/cancelled, blue = waiting, amber = needs
/// attention, green = available/done, red = failed/not available); media
/// type is conveyed by icon instead of color so the two never compete for
/// the same visual channel. See docs/07-ui-conventions.md before changing
/// this mapping, and prefer the FormatStatusChip, RequestStatusChip, or
/// MediaTypeChip component (FamilyLibrarian.Web.Client.Requests) over a
/// hand-rolled MudChip.
/// </summary>
public static class MediaTypeVisuals
{
    public static string KindleLabel(string status, string? confirmationStatus = null) => status switch
    {
        "Submitted" => confirmationStatus switch
        {
            "Confirmed" => "Received",
            "ReportedMissing" => "Sent, but not received",
            _ => "Sent — receipt unconfirmed"
        },
        "Pending" => "Waiting to send",
        "Submitting" => "Sending",
        "Failed" => "Delivery failed",
        "SubmissionUnknown" => "Send outcome unknown",
        "Cancelled" => "Cancelled",
        _ => status
    };

    public static Color KindleColor(string status, string? confirmationStatus = null) => status switch
    {
        "Submitted" => confirmationStatus switch
        {
            "Confirmed" => Color.Success,
            "ReportedMissing" => Color.Error,
            _ => Color.Info
        },
        "SubmissionUnknown" => Color.Warning,
        "Pending" or "Submitting" => Color.Info,
        "Failed" => Color.Error,
        _ => Color.Default
    };

    // ---- Provider-activity outcomes -----------------------------------------
    // The provider-activity ledger records one row per automatic lookup. Its
    // outcome vocabulary is its own (it is not a request or format status), so
    // it has its own mapping here rather than being folded into StatusColor --
    // but it follows the same colour rule: blue is "in progress, nothing to
    // do", amber is "needs attention", green is done, red is a real failure.
    // The crucial distinction is Submitted/Retrying (blue) versus Failed (red):
    // the retry loop deliberately moves on from a bad copy without a person,
    // and drawing that as an error taught administrators to ignore red.

    public static Color AttemptOutcomeColor(string outcome) => outcome switch
    {
        "Acquired" => Color.Success,
        "Submitted" or "Retrying" or "CandidatesFound" => Color.Info,
        "Blocked" => Color.Warning,
        "Failed" => Color.Error,
        _ => Color.Default
    };

    public static string AttemptOutcomeLabel(string outcome) => outcome switch
    {
        "NoMatch" => "Nothing found",
        "CandidatesFound" => "Candidates found",
        "Acquired" => "Acquired",
        "Submitted" => "In progress",
        "Retrying" => "Trying next copy",
        "Failed" => "Failed",
        "Blocked" => "Blocked",
        _ => outcome
    };

    /// <summary>
    /// Says in one line whether a person needs to do anything, because the
    /// colour alone has never been enough to tell an error from a step working
    /// as designed.
    /// </summary>
    public static string AttemptOutcomeHint(string outcome) => outcome switch
    {
        "NoMatch" => "The source had nothing for this request. It will be asked again later; nothing needs doing.",
        "CandidatesFound" => "The source found possible copies. A librarian chooses among them.",
        "Acquired" => "A copy was fetched and sent through the security checks.",
        "Submitted" => "A copy is being fetched. Nothing needs doing.",
        "Retrying" => "A copy didn't work out and the next best one is already being tried. Nothing needs doing.",
        "Failed" => "This did not work and a librarian needs to look at it.",
        "Blocked" => "A setting stopped this lookup. Check the source's configuration.",
        _ => outcome
    };

    /// <summary>
    /// An icon so the state is not carried by colour alone (colour-blind
    /// administrators, and the text-only places this row is copied into).
    /// </summary>
    public static string AttemptOutcomeIcon(string outcome) => outcome switch
    {
        "Acquired" => Icons.Material.Filled.CheckCircle,
        "Submitted" => Icons.Material.Filled.HourglassEmpty,
        "Retrying" => Icons.Material.Filled.Sync,
        "Failed" => Icons.Material.Filled.Error,
        "Blocked" => Icons.Material.Filled.Block,
        "CandidatesFound" => Icons.Material.Filled.Search,
        "NoMatch" => Icons.Material.Filled.SearchOff,
        _ => Icons.Material.Filled.Info
    };

    public static string Icon(string mediaType) => mediaType switch
    {
        "Ebook" => Icons.Material.Filled.MenuBook,
        "Audiobook" => Icons.Material.Filled.Headphones,
        _ => Icons.Material.Filled.Description
    };

    /// <summary>
    /// Covers both request-level status (PendingAcquisition, NeedsReview,
    /// NotAvailable, Cancelled, Available) and format-level status
    /// (Requested, NotAvailable, Cancelled, Available) — the two vocabularies
    /// share the same meaning for every value except PendingAcquisition/
    /// Requested, which fall through to <paramref name="progressCode"/> for a
    /// more specific color once acquisition is under way.
    /// </summary>
    public static Color StatusColor(string status, string? progressCode = null) => status switch
    {
        "Available" or "Trusted" or "Archived" or "Passed" => Color.Success,
        "Quarantine" or "Processing" or "Scanning" or "AwaitingScan" => Color.Info,
        "Unmatched" or "ReviewRequired" or "ScanInterrupted" or "ScanIncomplete" => Color.Warning,
        "Rejected" or "Destroyed" or "Failed" => Color.Error,
        "NeedsReview" => Color.Warning,
        "NotAvailable" => Color.Error,
        "Cancelled" => Color.Default,
        "PendingAcquisition" or "Requested" => ProgressColor(progressCode),
        _ => Color.Default
    };

    public static Color ProgressColor(string? progressCode) => progressCode switch
    {
        "SecurityCheckFailed" or "PublishingNeedsAttention" or "AcquisitionFailed" => Color.Error,
        // Moving on to the next copy is the system working, not a failure.
        "AcquisitionRetrying" => Color.Info,
        "AwaitingApproval" or "SecurityReviewRequired" or "IdentityReviewRequired" or "AwaitingProviderAction" => Color.Warning,
        "Available" => Color.Success,
        _ => Color.Info
    };

    /// <summary>Short, scannable label for a status chip. Not the same as the
    /// family-facing StatusDescription sentence, which stays as-is on pages
    /// where a full sentence reads naturally to the person who asked.</summary>
    public static string StatusLabel(string status) => status switch
    {
        "SubmissionUnknown" => "Send outcome unknown",
        "AwaitingScan" => "Awaiting scan",
        "NotScanned" => "No scan recorded",
        "ScanInterrupted" => "Retry required",
        "ScanIncomplete" => "Scan incomplete",
        "ReviewRequired" => "Review required",
        "Passed" => "Scan passed",
        "Failed" => "Scan failed",
        "Destroyed" => "File deleted",
        "Unmatched" => "Identity review required",
        "PendingAcquisition" or "Requested" => "Waiting",
        "NeedsReview" => "Needs review",
        "NotAvailable" => "Not available",
        "Cancelled" => "Cancelled",
        "Available" => "Available",
        _ => status
    };

    /// <summary>
    /// A search-result availability badge's color, keyed by
    /// <c>FulfillmentOptionResponse.OptionKind</c> -- a distinct vocabulary
    /// from request/format status above (a search candidate hasn't been
    /// requested yet), kept as its own mapping rather than folded into
    /// <see cref="StatusColor"/> so the two vocabularies can't be confused.
    /// </summary>
    public static Color OptionKindColor(string optionKind) => optionKind switch
    {
        "Owned" => Color.Success,
        "DirectAcquisition" or "Availability" => Color.Info,
        "StoreOffer" or "ExternalAction" => Color.Default,
        _ => Color.Default
    };

    public static string OptionKindLabel(string optionKind) => optionKind switch
    {
        "Owned" => "In your library",
        "DirectAcquisition" => "Free to get",
        "Availability" => "Available",
        "StoreOffer" => "For purchase",
        "ExternalAction" => "Found elsewhere",
        _ => optionKind
    };
}
