using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Tests.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-d step 2: each new call records the situation it was made in, once, so "kept" can be told from "never had a chance".</summary>
[TestClass]
public sealed class OpportunityFlagTests
{
    private const string Keep = "{\"schema\":\"candidate-triage/1\",\"move\":\"keep\",\"candidate\":null,\"evidence_ids\":[],\"note\":\"Nothing supports a move.\"}";
    private static readonly BookIdentity Requested = CaptureData.Requested;

    private static CandidateTriageContext Context(bool pick, int tied) =>
        new(Requested, [CaptureData.Option(Requested, 1, "The Lantern Road", "Mara Quill", "R.1"), CaptureData.Option(Requested, 2, "Another Book", "Mara Quill", "R.2")],
            pick ? CaptureData.Option(Requested, 1, "The Lantern Road", "Mara Quill", "R.1") : null,
            tied >= 2 ? ["result-1", "result-2"] : [], ProviderConfidenceLevel.Standard);

    private static async Task<(RecordingWorkflowObserver Observer, CaptureRig Rig)> RunTriageAsync(CandidateTriageContext context)
    {
        var rig = new CaptureRig(ModelRole.CandidateTriage, (_, _) => ScriptedModel.Ok(Keep), ModelReasoningMode.Inherit);
        var observer = new RecordingWorkflowObserver();
        var service = new CandidateTriageService(rig.Resolver, rig.Policy, new FakeFindingStore(), rig.Clock, observer);
        await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), "example-source", context, CancellationToken.None);
        return (observer, rig);
    }

    [TestMethod]
    public async Task ATriageCallWithAPickAndATieNotesBothOpportunitiesAlongsideItsAgreement()
    {
        var (observer, rig) = await RunTriageAsync(Context(pick: true, tied: 2));

        var recordId = rig.Ledger.Records.Single().Id;
        var effects = observer.Observations.Where(o => o.Id == recordId).Select(o => o.Effects).Aggregate(ModelWorkflowEffect.None, (a, b) => a | b);
        Assert.IsTrue(effects.HasFlag(ModelWorkflowEffect.TriageVetoEligible), "A pick existed, so a veto was possible.");
        Assert.IsTrue(effects.HasFlag(ModelWorkflowEffect.TriageTiedPreferEligible), "Two candidates were tied.");
        Assert.IsTrue(effects.HasFlag(ModelWorkflowEffect.NoChange), "The model kept the pick; that is still recorded as before.");
        Assert.IsFalse(effects.HasFlag(ModelWorkflowEffect.TriageAuthorOnlyConflictEligible));
        Assert.AreEqual(1, observer.Observations.Count(o => (o.Effects & ModelWorkflowEffect.AllOpportunities) != 0), "Written once per call.");
    }

    [TestMethod]
    public async Task ATriageCallWithNoPickAndNoTieNotesNoOpportunityBecauseItNeverHadOne()
    {
        // No pick, no tie, and the candidates are not an author-only conflict: the service makes no model call at all.
        var (observer, rig) = await RunTriageAsync(Context(pick: false, tied: 0));

        Assert.IsEmpty(rig.Model.Calls);
        Assert.IsEmpty(observer.Observations);
    }

    [TestMethod]
    public async Task AReusedTriageDecisionWritesNoSecondFlagBecauseNoNewCallWasMade()
    {
        var rig = new CaptureRig(ModelRole.CandidateTriage, (_, _) => ScriptedModel.Ok(Keep), ModelReasoningMode.Inherit);
        var observer = new RecordingWorkflowObserver();
        var service = new CandidateTriageService(rig.Resolver, rig.Policy, new FakeFindingStore(), rig.Clock, observer);
        var request = Guid.NewGuid();
        var format = Guid.NewGuid();
        await service.GetAsync(request, format, "example-source", Context(true, 2), CancellationToken.None);
        var before = observer.Observations.Count(o => (o.Effects & ModelWorkflowEffect.AllOpportunities) != 0);

        await service.GetAsync(request, format, "example-source", Context(true, 2), CancellationToken.None);

        Assert.AreEqual(1, rig.Model.Calls.Count);
        Assert.AreEqual(before, observer.Observations.Count(o => (o.Effects & ModelWorkflowEffect.AllOpportunities) != 0));
    }

    [TestMethod]
    public async Task ASearchGuidanceCallIsNotedAsMadeAfterTheFixedSearches()
    {
        var rig = new CaptureRig(ModelRole.SearchGuidance, (_, _) => ScriptedModel.Ok(
            "{\"schema\":\"search-guidance/1\",\"give_up\":false,\"suggestions\":[{\"reason\":\"alternate_published_title\",\"value\":\"A New Journey\",\"evidence_ids\":[\"req-title\"],\"note\":\"Try a new title.\"}]}"),
            ModelReasoningMode.Inherit);
        var observer = new RecordingWorkflowObserver();
        var service = new SearchGuidanceService(rig.Resolver, rig.Policy, new FakeFindingStore(), rig.Clock, observer);

        await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), Requested, CaptureData.SearchResults(Requested), ["Lantern Road"], CaptureRig.Now, CancellationToken.None);

        Assert.AreEqual(rig.Ledger.Records.Single().Id, observer.Observations.Single().Id);
        Assert.AreEqual(ModelWorkflowEffect.SearchAfterFixedSearches, observer.Observations.Single().Effects);
    }

    [TestMethod]
    public async Task AHoldReviewCallIsNotedAsHavingHadTheChanceToHold()
    {
        var rig = new CaptureRig(ModelRole.ArtifactValidationDecision, (_, _) => ScriptedModel.Ok(ArtifactDecisionKit.Reply("no_objection", id: "idv.ev-concern")),
            ModelReasoningMode.Inherit);
        var observer = new RecordingWorkflowObserver();
        var service = new ArtifactDecisionService(rig.Resolver, rig.Policy, new FakeFindingStore(), rig.Clock, workflow: observer);
        var verification = AssetIdentityVerificationResult.Match("test",
        [
            new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
            new("ev-concern", EvidenceDimension.Profile, EvidenceReasonCodes.ProfileComparison.LengthBelowExpected, EvidencePolarity.Weak, "words=12000;pages=100")
        ]);

        await service.GetHoldAsync(ArtifactDecisionKit.Asset(), verification, ArtifactDecisionKit.Expected, CancellationToken.None);

        var effects = observer.Observations.Select(o => o.Effects).Aggregate(ModelWorkflowEffect.None, (a, b) => a | b);
        Assert.IsTrue(effects.HasFlag(ModelWorkflowEffect.HoldReviewEligible));
    }

    [TestMethod]
    public void OpportunityFlagsAreNeverActionsAndEveryFlagIsCoveredByTheMask()
    {
        var opportunity = (int)ModelWorkflowEffect.AllOpportunities;
        Assert.AreEqual(0, opportunity & (int)ModelWorkflowEffects.Actions);
        Assert.AreEqual(0, opportunity & (int)ModelWorkflowEffect.All);
        var flags = new[]
        {
            ModelWorkflowEffect.TriageAuthorOnlyConflictEligible, ModelWorkflowEffect.TriageTiedPreferEligible, ModelWorkflowEffect.TriageVetoEligible,
            ModelWorkflowEffect.HoldReviewEligible, ModelWorkflowEffect.SearchAfterFixedSearches
        };
        Assert.AreEqual(ModelWorkflowEffect.AllOpportunities, flags.Aggregate(ModelWorkflowEffect.None, (a, b) => a | b));
        var record = new ModelUsageRecord(ModelRole.CandidateTriage, ModelBudgetClass.BackgroundValidation, null, "m", false, 1, DateTimeOffset.UtcNow);
        record.Complete(ModelCallOutcome.Succeeded, 1, null, null, null);
        record.ObserveWorkflow(ModelWorkflowEffect.TriageVetoEligible);
        Assert.AreEqual((int?)ModelWorkflowEffect.TriageVetoEligible, record.WorkflowEffects);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => record.ObserveWorkflow((ModelWorkflowEffect)(1 << 20)));
    }
}
