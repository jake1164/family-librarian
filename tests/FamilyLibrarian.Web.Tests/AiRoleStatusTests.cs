using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.ModelGateway;

namespace FamilyLibrarian.Web.Tests;

/// <summary>C1-d step 1: the per-feature status is a fixed rule over counts, states its reason, and is not a quality score.</summary>
[TestClass]
public sealed class AiRoleStatusTests
{
    private static ModelUsageRowResponse Row(string outcome, int calls, params string[] effects) =>
        new("CandidateTriage", "BackgroundValidation", outcome, false, calls, 0, 0, "m", null, effects.Length == 0 ? null : effects);

    [TestMethod]
    public void NoCallsIsNotUsedWithAReason()
    {
        var status = AiRoleStatus.From([]);

        Assert.AreEqual(AiRoleStatusKind.NotUsed, status.Kind);
        StringAssert.Contains(status.Reason, "No calls");
    }

    [TestMethod]
    public void FewerThanTwentyAcceptedCallsIsTooLittleDataEvenWhenTheyAllActed()
    {
        var status = AiRoleStatus.From([Row("Succeeded", 19, "CandidateVetoUsed")]);

        Assert.AreEqual(AiRoleStatusKind.TooLittleData, status.Kind);
        StringAssert.Contains(status.Reason, "19 accepted calls");
        StringAssert.Contains(status.Reason, "at least 20");
        Assert.AreEqual(AiRoleStatusKind.DoingWork, AiRoleStatus.From([Row("Succeeded", 20, "CandidateVetoUsed")]).Kind, "Twenty is enough.");
    }

    [TestMethod]
    public void AnAppliedActionIsDoingWorkAndSavedAdviceIsToo()
    {
        var acting = AiRoleStatus.From([Row("Succeeded", 30, "CandidateSelectionUsed"), Row("Succeeded", 5, "NoChange")]);
        var advising = AiRoleStatus.From([Row("Succeeded", 25, "AdvisorySaved")]);

        Assert.AreEqual(AiRoleStatusKind.DoingWork, acting.Kind);
        StringAssert.Contains(acting.Reason, "30 of 35");
        Assert.AreEqual(AiRoleStatusKind.DoingWork, advising.Kind);
        StringAssert.Contains(advising.Reason, "Advice was saved");
    }

    [TestMethod]
    public void AgreeingAbstainingOrChangingNothingIsSafeButIdle()
    {
        var status = AiRoleStatus.From([Row("Succeeded", 22, "NoChange"), Row("Succeeded", 3, "Abstained")]);

        Assert.AreEqual(AiRoleStatusKind.SafeButIdle, status.Kind);
        StringAssert.Contains(status.Reason, "no action was applied");
    }

    [TestMethod]
    public void MoreThanHalfOfAtLeastTwentyEvaluatedCallsRejectedOrErroredIsFailing()
    {
        var failing = AiRoleStatus.From([Row("Succeeded", 10), Row("SchemaRejected", 20), Row("Timeout", 5)]);
        var half = AiRoleStatus.From([Row("Succeeded", 20, "NoChange"), Row("SchemaRejected", 20)]);

        Assert.AreEqual(AiRoleStatusKind.Failing, failing.Kind);
        StringAssert.Contains(failing.Reason, "25 of 35");
        Assert.AreNotEqual(AiRoleStatusKind.Failing, half.Kind, "Exactly half is not more than half.");
    }

    [TestMethod]
    public void TooFewEvaluatedCallsCannotMakeAFeatureFailingAndPendingOrCancelledCallsDoNotCount()
    {
        var fewFailures = AiRoleStatus.From([Row("SchemaRejected", 19)]);
        var cancelled = new ModelUsageRowResponse("CandidateTriage", "BackgroundValidation", "TransportFailure", false, 40, 0, 0, "m", "Cancelled");
        var pending = Row("Pending", 40);

        Assert.AreEqual(AiRoleStatusKind.TooLittleData, fewFailures.Kind);
        Assert.AreEqual(AiRoleStatusKind.TooLittleData, AiRoleStatus.From([cancelled, pending, Row("Succeeded", 3)]).Kind);
    }

    [TestMethod]
    public void TheStatusNamesItselfInPlainWordsAndNeverClaimsCorrectness()
    {
        foreach (var rows in new[] { new[] { Row("Succeeded", 25, "CandidateVetoUsed") }, new[] { Row("Succeeded", 25, "NoChange") }, new[] { Row("SchemaRejected", 30) } })
        {
            var status = AiRoleStatus.From(rows);
            foreach (var word in new[] { "correct", "accurate", "best", "good model", "quality" })
                Assert.IsFalse((status.Label + " " + status.Reason).Contains(word, StringComparison.OrdinalIgnoreCase), word);
        }
    }
}
