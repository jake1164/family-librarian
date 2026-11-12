using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

/// <summary>M4-4: the security pipeline only notes that evidence changed. Its result and every hold are exactly as before.</summary>
[TestClass]
public sealed class PipelineShadowHookTests
{
    private sealed class Runner(SecurityEvaluationStatus status) : ISecurityEvaluationRunner
    {
        public Task<SecurityEvaluationResult> EvaluateAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(SecurityEvaluationResult.Success(Guid.NewGuid(), status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    private sealed class Approvals(ApprovalResult result) : IPolicyAssetApprovalService
    {
        public int Calls { get; private set; }

        public Task<ApprovalResult> ApproveByPolicyAsync(Guid assetId, string policyName, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class Identity : IAssetIdentityVerificationService
    {
        public Task<AssetIdentityVerificationResult> VerifyAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(new AssetIdentityVerificationResult(false, "test"));

        public Task<AssetIdentityVerificationResult> RetryUnmatchedAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(new AssetIdentityVerificationResult(false, "test"));
    }

    private sealed class Shadow : IArtifactSemanticShadow
    {
        public List<Guid> Observed { get; } = [];

        public void Observe(Guid assetId) => Observed.Add(assetId);
    }

    [TestMethod]
    public async Task AnEvaluationNotesTheAssetForShadowReviewWithoutChangingTheResult()
    {
        var shadow = new Shadow();
        var approvals = new Approvals(ApprovalResult.IdentityUnmatched());
        var pipeline = new AutomatedSecurityPipeline(new Runner(SecurityEvaluationStatus.Passed), approvals, new Identity(), null, shadow);
        var assetId = Guid.NewGuid();

        var result = await pipeline.EvaluateAsync(assetId, CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationOutcome.Success, result.Outcome);
        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(1, approvals.Calls);
        CollectionAssert.AreEqual(new[] { assetId }, shadow.Observed);
    }

    [TestMethod]
    public async Task AFailedEvaluationIsStillNeverApprovedAndStillNotedOnlyForReview()
    {
        var shadow = new Shadow();
        var approvals = new Approvals(ApprovalResult.Success());
        var pipeline = new AutomatedSecurityPipeline(new Runner(SecurityEvaluationStatus.Failed), approvals, new Identity(), null, shadow);

        var result = await pipeline.EvaluateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Failed, result.Status);
        Assert.AreEqual(0, approvals.Calls, "Observing never changes the rule that a failed scan is not approved.");
    }

    [TestMethod]
    public async Task APipelineWithoutAShadowBehavesAsItAlwaysDid()
    {
        var approvals = new Approvals(ApprovalResult.Success());
        var pipeline = new AutomatedSecurityPipeline(new Runner(SecurityEvaluationStatus.Passed), approvals, new Identity());

        var result = await pipeline.EvaluateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(SecurityEvaluationStatus.Passed, result.Status);
        Assert.AreEqual(1, approvals.Calls);
    }
}
