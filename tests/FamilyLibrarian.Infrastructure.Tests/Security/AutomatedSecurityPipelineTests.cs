using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class AutomatedSecurityPipelineTests
{
    [TestMethod]
    public async Task APassedEvaluationIsApprovedByTheCleanScanPolicy()
    {
        var assetId = Guid.NewGuid();
        var approvals = new RecordingPolicyApprovalService();
        var pipeline = new AutomatedSecurityPipeline(
            new DeterministicEvaluationRunner(SecurityEvaluationStatus.Passed),
            approvals,
            new DeterministicIdentityVerificationService());

        var result = await pipeline.EvaluateAsync(assetId, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(assetId, approvals.AssetId);
        Assert.AreEqual("clean-security-evaluation-v1", approvals.PolicyName);
    }

    [TestMethod]
    public async Task AReviewRequiredEvaluationIsNotApprovedByPolicy()
    {
        var approvals = new RecordingPolicyApprovalService();
        var pipeline = new AutomatedSecurityPipeline(
            new DeterministicEvaluationRunner(SecurityEvaluationStatus.ReviewRequired),
            approvals,
            new DeterministicIdentityVerificationService());

        var result = await pipeline.EvaluateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.ReviewRequired, result.Status);
        Assert.IsNull(approvals.AssetId);
    }

    [TestMethod]
    public async Task AnUnmatchedIdentityDoesNotFailTheSecurityEvaluation()
    {
        var approvals = new RecordingPolicyApprovalService(ApprovalResult.IdentityUnmatched());
        var pipeline = new AutomatedSecurityPipeline(
            new DeterministicEvaluationRunner(SecurityEvaluationStatus.Passed),
            approvals,
            new DeterministicIdentityVerificationService());

        var result = await pipeline.EvaluateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.IsNotNull(approvals.AssetId);
    }

    [TestMethod]
    public async Task ARetriedMatchingIdentityIsApprovedByTheCleanScanPolicy()
    {
        var assetId = Guid.NewGuid();
        var approvals = new RecordingPolicyApprovalService();
        var identity = new DeterministicIdentityVerificationService(AssetIdentityVerificationResult.Match("test"));
        var pipeline = new AutomatedSecurityPipeline(
            new DeterministicEvaluationRunner(SecurityEvaluationStatus.Passed), approvals, identity);

        var result = await pipeline.RetryIdentityAsync(assetId, CancellationToken.None);

        Assert.AreEqual(ApprovalOutcome.Success, result.Outcome);
        Assert.AreEqual(assetId, identity.RetriedAssetId);
        Assert.AreEqual(assetId, approvals.AssetId);
        Assert.AreEqual("clean-security-evaluation-v1", approvals.PolicyName);
    }

    private sealed class DeterministicEvaluationRunner(SecurityEvaluationStatus status) : ISecurityEvaluationRunner
    {
        public Task<SecurityEvaluationResult> EvaluateAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(SecurityEvaluationResult.Success(Guid.NewGuid(), status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    private sealed class RecordingPolicyApprovalService(ApprovalResult? result = null) : IPolicyAssetApprovalService
    {
        public Guid? AssetId { get; private set; }

        public string? PolicyName { get; private set; }

        public Task<ApprovalResult> ApproveByPolicyAsync(
            Guid assetId,
            string policyName,
            CancellationToken cancellationToken)
        {
            AssetId = assetId;
            PolicyName = policyName;
            return Task.FromResult(result ?? ApprovalResult.Success());
        }
    }

    private sealed class DeterministicIdentityVerificationService(
        AssetIdentityVerificationResult? retryResult = null) : IAssetIdentityVerificationService
    {
        public Guid? RetriedAssetId { get; private set; }

        public Task<AssetIdentityVerificationResult> VerifyAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(AssetIdentityVerificationResult.Match("test"));

        public Task<AssetIdentityVerificationResult> RetryUnmatchedAsync(
            Guid assetId,
            CancellationToken cancellationToken)
        {
            RetriedAssetId = assetId;
            return Task.FromResult(retryResult ?? AssetIdentityVerificationResult.Match("test"));
        }
    }
}
