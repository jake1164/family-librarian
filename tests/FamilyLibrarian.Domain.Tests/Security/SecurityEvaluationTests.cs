using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Domain.Tests.Security;

[TestClass]
public sealed class SecurityEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AllCleanRequiredScansAndValidFormatsPass()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, "1.4", Now);
        evaluation.RecordValidationResult("epub", isValid: true, null, Now);

        evaluation.Evaluate(Now);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, evaluation.Status);
    }

    [TestMethod]
    public void ARequiredScannerReportingErrorNeverPasses()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Error, null, null, Now);

        evaluation.Evaluate(Now);

        Assert.AreNotEqual(SecurityEvaluationStatus.Passed, evaluation.Status);
        Assert.AreEqual(SecurityEvaluationStatus.ReviewRequired, evaluation.Status);
    }

    [TestMethod]
    public void ARequiredScannerReportingUnavailableNeverPasses()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Unavailable, null, null, Now);

        evaluation.Evaluate(Now);

        Assert.AreNotEqual(SecurityEvaluationStatus.Passed, evaluation.Status);
        Assert.AreEqual(SecurityEvaluationStatus.ReviewRequired, evaluation.Status);
    }

    [TestMethod]
    public void ADetectedThreatFails()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Detected, "Eicar-Test-Signature", null, Now);

        evaluation.Evaluate(Now);

        Assert.AreEqual(SecurityEvaluationStatus.Failed, evaluation.Status);
    }

    [TestMethod]
    public void ADetectedThreatFromAnOptionalScannerStillFailsClosed()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now);
        evaluation.RecordScanResult("optional-scanner", isRequired: false, ScanResultStatus.Detected, "Test threat", null, Now);

        evaluation.Evaluate(Now);

        Assert.AreEqual(SecurityEvaluationStatus.Failed, evaluation.Status);
    }

    [TestMethod]
    public void AnInvalidFormatFails()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now);
        evaluation.RecordValidationResult("epub", isValid: false, "Corrupt ZIP central directory.", Now);

        evaluation.Evaluate(Now);

        Assert.AreEqual(SecurityEvaluationStatus.Failed, evaluation.Status);
    }

    [TestMethod]
    public void AnUnrequiredScannerErrorDoesNotBlockPassing()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now);
        evaluation.RecordScanResult("optional-scanner", isRequired: false, ScanResultStatus.Error, null, null, Now);

        evaluation.Evaluate(Now);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, evaluation.Status);
    }

    [TestMethod]
    public void EvaluatingTwiceThrows()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now);
        evaluation.Evaluate(Now);

        Assert.ThrowsExactly<InvalidOperationException>(() => evaluation.Evaluate(Now));
    }

    [TestMethod]
    public void ARecordAfterEvaluatingThrows()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now);
        evaluation.Evaluate(Now);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now));
    }

    [TestMethod]
    public void ApprovingAPendingEvaluationThrows()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            evaluation.Approve(ApprovalActorType.Admin, Guid.NewGuid(), null, null, Now));
    }

    [TestMethod]
    public void ApprovingAFailedEvaluationIsNeverAllowedEvenForAnAdmin()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Detected, "eicar", null, Now);
        evaluation.Evaluate(Now);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            evaluation.Approve(ApprovalActorType.Admin, Guid.NewGuid(), null, "override attempt", Now));
    }

    [TestMethod]
    public void OnlyAnAdminMayApproveAReviewRequiredEvaluation()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Unavailable, null, null, Now);
        evaluation.Evaluate(Now);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            evaluation.Approve(ApprovalActorType.Policy, null, "auto-policy", null, Now));

        var approval = evaluation.Approve(ApprovalActorType.Admin, Guid.NewGuid(), null, "reviewed manually", Now);
        Assert.AreEqual(ApprovalDecision.Approved, approval.Decision);
    }

    [TestMethod]
    public void APassedEvaluationCanBeApprovedByPolicy()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Clean, null, null, Now);
        evaluation.Evaluate(Now);

        var approval = evaluation.Approve(ApprovalActorType.Policy, null, "auto-approve-clean", null, Now);

        Assert.AreEqual(ApprovalDecision.Approved, approval.Decision);
    }

    [TestMethod]
    public void RejectingIsAlwaysAllowedRegardlessOfStatus()
    {
        var evaluation = new SecurityEvaluation(Guid.NewGuid(), "v1", Now);
        evaluation.RecordScanResult("clamav", isRequired: true, ScanResultStatus.Detected, "eicar", null, Now);
        evaluation.Evaluate(Now);

        var rejection = evaluation.Reject(ApprovalActorType.Admin, Guid.NewGuid(), "confirmed malicious", Now);

        Assert.AreEqual(ApprovalDecision.Rejected, rejection.Decision);
    }
}
