using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Requests;

[TestClass]
public sealed class RequestFormatProgressTests
{
    [TestMethod]
    public void SecurityStagesHaveRequesterSafeProgress()
    {
        var cases = new (MediaAssetStorageState AssetState, SecurityEvaluationStatus? SecurityStatus, string Code)[]
        {
            (MediaAssetStorageState.Quarantine, null, "AwaitingSecurityScan"),
            (MediaAssetStorageState.Processing, null, "SecurityScanInProgress"),
            (MediaAssetStorageState.Processing, SecurityEvaluationStatus.Passed, "AwaitingApproval"),
            (MediaAssetStorageState.Processing, SecurityEvaluationStatus.ReviewRequired, "SecurityReviewRequired"),
            (MediaAssetStorageState.Rejected, null, "SecurityCheckFailed")
        };

        foreach (var testCase in cases)
        {
            var result = RequestFormatProgress.Describe(
                testCase.AssetState,
                testCase.SecurityStatus,
                null,
                null);

            Assert.IsNotNull(result);
            Assert.AreEqual(testCase.Code, result.Code);
            Assert.DoesNotContain("filename", result.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    public void CwaStagesAreShownWithoutDestinationFailureDetails()
    {
        var cases = new (LibraryImportStatus ImportStatus, string Code)[]
        {
            (LibraryImportStatus.Publishing, "Publishing"),
            (LibraryImportStatus.AwaitingVerification, "AwaitingLibraryVerification"),
            (LibraryImportStatus.Failed, "PublishingNeedsAttention")
        };

        foreach (var testCase in cases)
        {
            var result = RequestFormatProgress.Describe(
                MediaAssetStorageState.Trusted,
                SecurityEvaluationStatus.Passed,
                testCase.ImportStatus,
                null);

            Assert.IsNotNull(result);
            Assert.AreEqual(testCase.Code, result.Code);
            Assert.DoesNotContain("error", result.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    public void AWaitingProviderJobIsShownAsAwaitingProviderActionRegardlessOfPhase()
    {
        var result = RequestFormatProgress.Describe(
            assetState: null,
            securityStatus: null,
            libraryImportStatus: null,
            deliveryStatus: null,
            providerJobState: ProviderAcquisitionJobLifecycleState.Waiting,
            providerJobPhase: "user-interaction");

        Assert.IsNotNull(result);
        Assert.AreEqual("AwaitingProviderAction", result.Code);
    }

    [TestMethod]
    public void AQueuedOrRunningProviderJobIsShownAsAcquisitionInProgress()
    {
        foreach (var state in new[] { ProviderAcquisitionJobLifecycleState.Queued, ProviderAcquisitionJobLifecycleState.Running })
        {
            var result = RequestFormatProgress.Describe(
                assetState: null,
                securityStatus: null,
                libraryImportStatus: null,
                deliveryStatus: null,
                providerJobState: state,
                providerJobPhase: "downloading");

            Assert.IsNotNull(result);
            Assert.AreEqual("AcquisitionInProgress", result.Code);
        }
    }

    [TestMethod]
    public void AnExistingAssetTakesPrecedenceOverAProviderJobState()
    {
        // Once a MediaAsset exists, the ordinary asset-state pipeline governs
        // -- a provider job lingering in a non-terminal state must never
        // override real, more-advanced progress.
        var result = RequestFormatProgress.Describe(
            MediaAssetStorageState.Quarantine,
            securityStatus: null,
            libraryImportStatus: null,
            deliveryStatus: null,
            providerJobState: ProviderAcquisitionJobLifecycleState.Running,
            providerJobPhase: "downloading");

        Assert.IsNotNull(result);
        Assert.AreEqual("AwaitingSecurityScan", result.Code);
    }
}
