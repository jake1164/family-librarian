using FamilyLibrarian.Contracts.Requests;

namespace FamilyLibrarian.Web.Client.Requests;

/// <summary>
/// The short word beside a request's title on the admin pages. It must agree with the format chips next to it: on the lab
/// a book whose ebook read "Fetching from the external provider" still showed "Awaiting next check", because the label
/// counted only Family Librarian's own downloads and an external provider's download is not among them.
/// </summary>
public static class RequestProgressLabels
{
    /// <summary>Progress codes that mean work on the request is happening right now.</summary>
    private static readonly HashSet<string> WorkingCodes = new(StringComparer.Ordinal)
    {
        "AcquisitionInProgress",
        "AwaitingSecurityScan",
        "SecurityScanInProgress",
        "AwaitingPublishing",
        "Publishing",
        "AwaitingLibraryVerification"
    };

    /// <summary>"Working" or "Awaiting next check" for a request still being acquired; null for any other status.</summary>
    public static string? For(BookRequestResponse request, bool hasActiveAcquisition)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status != "PendingAcquisition")
        {
            return null;
        }

        return hasActiveAcquisition || request.Formats.Any(format => format.ProgressCode is { } code && WorkingCodes.Contains(code))
            ? "Working"
            : "Awaiting next check";
    }
}
