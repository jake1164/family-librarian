using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// Requester-safe progress for one requested format.
/// </summary>
/// <remarks>
/// This deliberately describes the workflow stage rather than exposing an
/// uploaded filename, scanner output, destination error, or librarian-only
/// review note.
/// </remarks>
public sealed record RequestFormatProgressView(string Code, string Description);

/// <summary>
/// Converts internal asset, security, and publishing states into the small,
/// stable set of progress messages shown to the requester.
/// </summary>
public static class RequestFormatProgress
{
    public static RequestFormatProgressView? Describe(
        MediaAssetStorageState? assetState,
        SecurityEvaluationStatus? securityStatus,
        LibraryImportStatus? libraryImportStatus,
        DeliveryStatus? deliveryStatus) => assetState switch
    {
        null => null,
        MediaAssetStorageState.Quarantine => Stage(
            "AwaitingSecurityScan",
            "File received — awaiting security scan."),
        MediaAssetStorageState.Processing => DescribeProcessing(securityStatus),
        MediaAssetStorageState.Rejected => Stage(
            "SecurityCheckFailed",
            "The submitted file did not pass security checks."),
        MediaAssetStorageState.Trusted => DescribePublishing(libraryImportStatus, deliveryStatus),
        MediaAssetStorageState.Archived => Stage(
            "Available",
            "Available in the family library."),
        MediaAssetStorageState.Unmatched => Stage(
            "IdentityReviewRequired",
            "The file needs librarian identification before it can be delivered."),
        MediaAssetStorageState.Destroyed => Stage(
            "FileRemoved",
            "The submitted file was removed before delivery."),
        _ => null
    };

    private static RequestFormatProgressView DescribeProcessing(SecurityEvaluationStatus? securityStatus) =>
        securityStatus switch
        {
            SecurityEvaluationStatus.Passed => Stage(
                "AwaitingApproval",
                "Security checks passed — awaiting approval."),
            SecurityEvaluationStatus.ReviewRequired => Stage(
                "SecurityReviewRequired",
                "Security review is required before delivery."),
            SecurityEvaluationStatus.Failed => Stage(
                "SecurityCheckFailed",
                "The submitted file did not pass security checks."),
            _ => Stage(
                "SecurityScanInProgress",
                "Security checks are in progress.")
        };

    private static RequestFormatProgressView DescribePublishing(
        LibraryImportStatus? libraryImportStatus,
        DeliveryStatus? deliveryStatus)
    {
        if (libraryImportStatus is not null)
        {
            return libraryImportStatus switch
            {
                LibraryImportStatus.Publishing => Stage(
                    "Publishing",
                    "Approved — publishing to CWA."),
                LibraryImportStatus.AwaitingVerification => Stage(
                    "AwaitingLibraryVerification",
                    "Uploaded to CWA — waiting for it to appear in the library."),
                LibraryImportStatus.Available => Stage(
                    "Available",
                    "Available in the family library."),
                LibraryImportStatus.Failed => Stage(
                    "PublishingNeedsAttention",
                    "Publishing needs the librarian's attention."),
                _ => Stage("AwaitingPublishing", "Approved — waiting to publish.")
            };
        }

        if (deliveryStatus is not null)
        {
            return deliveryStatus switch
            {
                DeliveryStatus.Uploading => Stage(
                    "Publishing",
                    "Approved — publishing to Audiobookshelf."),
                DeliveryStatus.Verifying => Stage(
                    "AwaitingLibraryVerification",
                    "Uploaded to Audiobookshelf — waiting for it to appear in the library."),
                DeliveryStatus.Delivered => Stage(
                    "Available",
                    "Available in the family library."),
                DeliveryStatus.Failed => Stage(
                    "PublishingNeedsAttention",
                    "Publishing needs the librarian's attention."),
                _ => Stage("AwaitingPublishing", "Approved — waiting to publish.")
            };
        }

        return Stage("AwaitingPublishing", "Approved — waiting to publish.");
    }

    private static RequestFormatProgressView Stage(string code, string description) => new(code, description);
}
