using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.ModelGateway;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AiQualityMetricsTests
{
    private static ModelUsageRowResponse Row(string outcome, int calls, string? reason = null, string[]? effects = null) =>
        new("ArtifactValidationDecision", "BackgroundValidation", outcome, false, calls, 0, calls, "example-model", reason, effects);

    [TestMethod]
    public void ObservedLabFailuresAreResponsiveButZeroPercentUsable()
    {
        var metrics = AiQualityMetrics.From([Row("SchemaRejected", 93, "UngroundedConcern"), Row("SchemaRejected", 6)]);
        Assert.AreEqual("100% (99/99)", metrics.ProviderRate);
        Assert.AreEqual("0% (0/99)", metrics.UsableRate);
        Assert.AreEqual("Replies rejected", metrics.Signal);
        Assert.AreEqual("Unsupported concerns: 93", metrics.RejectionIssue);
        Assert.AreEqual(99, metrics.UnusableReplies);
        Assert.AreEqual(0, metrics.ProviderErrors);
        Assert.IsNull(metrics.ProviderIssue);
    }

    [TestMethod]
    public void ProviderProblemsDoNotPretendTheReceivedAnswersAreBadAndPendingCancellationAreNotScored()
    {
        var metrics = AiQualityMetrics.From([Row("Succeeded", 2), Row("Timeout", 3), Row("Pending", 5), Row("TransportFailure", 4, "Cancelled")]);
        Assert.AreEqual("40% (2/5)", metrics.ProviderRate);
        Assert.AreEqual("100% (2/2)", metrics.UsableRate);
        Assert.AreEqual(4, metrics.Cancelled);
        Assert.AreEqual(5, metrics.Pending);
        Assert.AreEqual(0, metrics.Unclassified);
    }

    [TestMethod]
    public void ZeroCallsAndNoRepliesDoNotClaimZeroOrPerfectQuality()
    {
        Assert.AreEqual("Not used", AiQualityMetrics.From([]).Signal);
        Assert.AreEqual("No data", AiQualityMetrics.From([]).UsableRate);
        var unavailable = AiQualityMetrics.From([Row("TransportFailure", 3)]);
        Assert.AreEqual("Provider errors", unavailable.Signal);
        Assert.AreEqual("0% (0/3)", unavailable.ProviderRate);
        Assert.AreEqual("No data", unavailable.UsableRate);
        Assert.AreEqual("No evaluated responses", AiQualityMetrics.From([Row("Pending", 1)]).Signal);
    }

    [TestMethod]
    public void AHighRateDoesNotRoundAwayKnownFailures()
    {
        Assert.AreEqual(">99.9% (99,999/100,000)", AiQualityMetrics.Rate(99999, 100000));
        Assert.AreEqual("<0.1% (1/100,000)", AiQualityMetrics.Rate(1, 100000));
    }

    [TestMethod]
    public void EveryRecordedOutcomeHasOneCategoryAndDiagnosticsKeepRejectionsSeparateFromProviderErrors()
    {
        foreach (var outcome in Enum.GetValues<FamilyLibrarian.Domain.ModelGateway.ModelCallOutcome>())
        {
            var metrics = AiQualityMetrics.From([Row(outcome.ToString(), 1)]);
            Assert.AreEqual(1, metrics.Usable + metrics.UnusableReplies + metrics.ProviderErrors + metrics.Pending + metrics.Cancelled + metrics.Unclassified,
                $"{outcome} must belong to exactly one category.");
        }
        var mixed = AiQualityMetrics.From([Row("Succeeded", 2), Row("SchemaRejected", 93, "UngroundedConcern"),
            Row("SchemaRejected", 3, "RepeatedSearch"), Row("Timeout", 4), Row("TransportFailure", 5, "Cancelled"),
            Row("Pending", 6), Row("FutureOutcome", 7)]);
        Assert.AreEqual(96, mixed.UnusableReplies);
        Assert.AreEqual(4, mixed.ProviderErrors);
        Assert.AreEqual(5, mixed.Cancelled);
        Assert.AreEqual(7, mixed.Unclassified);
        Assert.AreEqual("Timeouts: 4", mixed.ProviderIssue);
        Assert.AreEqual("Unsupported concerns: 93", mixed.RejectionIssue);
        Assert.AreEqual("Provider errors and rejected replies", mixed.Signal);
        Assert.AreEqual("Cancelled", ModelCallCategories.Classify("TransportFailure", "Cancelled"));
        Assert.AreEqual("RejectedReply", ModelCallCategories.Classify("SchemaRejected", "RepeatedSearch"));
        Assert.AreEqual("AcceptedReply", ModelCallCategories.Classify("Succeeded", null));
    }

    [TestMethod]
    public void EffectsCountDistinctCallsAndDoNotEquateRecommendationsOrLegacyUnknownsWithActions()
    {
        var metrics = AiWorkflowMetrics.From([
            Row("Succeeded", 1, effects: ["ExtraSearchCompleted", "ExtraSearchReturnedResults"]),
            Row("Succeeded", 2, effects: ["NoChange"]), Row("Succeeded", 3, effects: ["Abstained"]),
            Row("Succeeded", 4, effects: ["AdvisorySaved"]), Row("Succeeded", 5), Row("SchemaRejected", 20)]);
        Assert.AreEqual(15, metrics.Usable);
        Assert.AreEqual(10, metrics.Observed);
        Assert.AreEqual(1, metrics.ActionCalls, "Two effects from one call are one used call, not two calls.");
        Assert.AreEqual(4, metrics.AdvisoryCalls);
        Assert.AreEqual(5, metrics.NoChangeCalls);
        Assert.AreEqual(5, metrics.NotObserved);
        Assert.AreEqual(2, metrics.Effects.Count);
        Assert.AreEqual("No objection", AiQualityMetrics.Recommendation("no_objection"));
    }

    [TestMethod]
    public void LaterUseDoesNotLeaveAReusedCallCountedAsNoChange()
    {
        var metrics = AiWorkflowMetrics.From([Row("Succeeded", 1, effects: ["NoChange", "CandidateSelectionUsed"])]);
        Assert.AreEqual(1, metrics.ActionCalls);
        Assert.AreEqual(0, metrics.NoChangeCalls);
    }
}
