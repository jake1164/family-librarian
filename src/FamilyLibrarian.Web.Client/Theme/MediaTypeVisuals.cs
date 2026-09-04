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
        "Available" => Color.Success,
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
        "PendingAcquisition" or "Requested" => "Waiting",
        "NeedsReview" => "Needs review",
        "NotAvailable" => "Not available",
        "Cancelled" => "Cancelled",
        "Available" => "Available",
        _ => status
    };
}
