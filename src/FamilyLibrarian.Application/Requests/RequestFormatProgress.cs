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
    /// <param name="providerJobState">
    /// The active <see cref="ProviderAcquisitionJob"/>'s lifecycle
    /// state, when one exists for this format and <paramref name="assetState"/>
    /// is still <c>null</c> -- no file exists yet, so none of the
    /// asset/security/publishing states below apply. Checked before
    /// <paramref name="assetState"/>'s own switch so a job that is
    /// <c>waiting</c>/<c>user-interaction</c> or simply still running shows
    /// real progress instead of the bare "Requested" state.
    /// </param>
    /// <param name="providerJobInteractionMessage">
    /// The provider's own explanation of what it is waiting on (protocol v2
    /// §8's <c>interaction.message</c>), shown verbatim in place of the
    /// generic waiting sentence when the provider supplied one -- otherwise
    /// an admin sees the same "action needed" chip for a CAPTCHA, a rate
    /// limit, and a manual review queue with no way to tell them apart.
    /// </param>
    public static RequestFormatProgressView? Describe(
        MediaAssetStorageState? assetState,
        SecurityEvaluationStatus? securityStatus,
        LibraryImportStatus? libraryImportStatus,
        AudiobookshelfDeliveryStatus? deliveryStatus,
        ProviderAcquisitionJobLifecycleState? providerJobState = null,
        string? providerJobPhase = null,
        string? providerJobInteractionMessage = null)
    {
        if (assetState is null && providerJobState is not null)
        {
            return DescribeProviderJob(providerJobState.Value, providerJobPhase, providerJobInteractionMessage);
        }

        return assetState switch
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
    }

    /// <summary>
    /// A durable job is <c>waiting</c> whenever it needs something external
    /// to proceed (protocol v2 §8) -- most concretely user interaction, but
    /// the state alone is enough to warrant the "action needed" treatment
    /// regardless of the exact open-string phase. <c>failed</c> is shown
    /// distinctly (not lumped into ordinary in-progress work) but, like
    /// <see cref="MediaAssetStorageState.Rejected"/>/<c>PublishingNeedsAttention</c>
    /// below, deliberately generic here -- this view is also read by the
    /// plain requester (<c>ListForUserAsync</c>), so the provider's raw error
    /// text belongs only in the admin-only Provider Activity ledger
    /// (<see cref="ProviderAttempt"/>), not on this shared chip.
    /// Any other non-terminal state (<c>queued</c>/<c>running</c>) is
    /// ordinary in-progress work.
    /// </summary>
    private static RequestFormatProgressView DescribeProviderJob(
        ProviderAcquisitionJobLifecycleState state, string? phase, string? interactionMessage) => state switch
    {
        ProviderAcquisitionJobLifecycleState.Waiting => Stage(
            "AwaitingProviderAction",
            string.IsNullOrWhiteSpace(interactionMessage)
                ? "Action is needed to continue fetching this from the provider."
                : interactionMessage),
        ProviderAcquisitionJobLifecycleState.Failed => Stage(
            "AcquisitionFailed",
            "The acquisition failed and needs the librarian's attention."),
        _ => Stage(
            "AcquisitionInProgress",
            "Fetching from the external provider.")
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
        AudiobookshelfDeliveryStatus? deliveryStatus)
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
                AudiobookshelfDeliveryStatus.Uploading => Stage(
                    "Publishing",
                    "Approved — publishing to Audiobookshelf."),
                AudiobookshelfDeliveryStatus.Verifying => Stage(
                    "AwaitingLibraryVerification",
                    "Uploaded to Audiobookshelf — waiting for it to appear in the library."),
                AudiobookshelfDeliveryStatus.Delivered => Stage(
                    "Available",
                    "Available in the family library."),
                AudiobookshelfDeliveryStatus.Failed => Stage(
                    "PublishingNeedsAttention",
                    "Publishing needs the librarian's attention."),
                _ => Stage("AwaitingPublishing", "Approved — waiting to publish.")
            };
        }

        return Stage("AwaitingPublishing", "Approved — waiting to publish.");
    }

    private static RequestFormatProgressView Stage(string code, string description) => new(code, description);
}
