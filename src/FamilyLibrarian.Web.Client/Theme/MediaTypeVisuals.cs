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
        "SecurityCheckFailed" or "PublishingNeedsAttention" => Color.Error,
        "AwaitingApproval" or "SecurityReviewRequired" or "IdentityReviewRequired" => Color.Warning,
        "Available" => Color.Success,
        _ => Color.Info
    };

    /// <summary>Short, scannable label for a status chip. Not the same as the
    /// family-facing StatusDescription sentence, which stays as-is on pages
    /// where a full sentence reads naturally to the person who asked.</summary>
    public static string StatusLabel(string status) => status switch
    {
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
