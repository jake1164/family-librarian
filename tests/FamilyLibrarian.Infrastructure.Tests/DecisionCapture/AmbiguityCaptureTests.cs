using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-b, candidate ambiguity: one unsettled candidate beside the request is saved after the advisory is stored.</summary>
[TestClass]
public sealed class AmbiguityCaptureTests
{
    private const string Uncertain = "{\"schema\":\"candidate-assessment/1\",\"verdict\":\"uncertain\",\"claims\":[{\"kind\":\"insufficient_information\",\"evidence_ids\":[],\"note\":\"Not enough evidence.\"}]}";

    private static CaptureRig Rig(bool capture = true) =>
        new(ModelRole.CandidateAmbiguityAssessment, (_, _) => ScriptedModel.Ok(Uncertain, model: "provider/ambig-4b"), ModelReasoningMode.Disabled, capture);

    private static CandidateIdentityAssessment Assessment()
    {
        var assessment = CaptureData.Option(CaptureData.Requested, 1, "The Lantern Road", "Mara Quill", "The.Lantern.Road.Sample", sample: true).IdentityAssessment!;
        Assert.IsTrue(CandidateSemanticEvidence.IsEligible(assessment), $"Test data must be an unsettled candidate, but the resolver said {assessment.Decision}.");
        return assessment;
    }

    private static CandidateSemanticAnalyzer Analyzer(CaptureRig rig, IDecisionCaptureSink? sink, FakeFindingStore? findings = null) =>
        new(rig.Policy, findings ?? new FakeFindingStore(), rig.Clock, null, sink);

    [TestMethod]
    public async Task AnUnsettledCandidateIsSavedAndReproducesThePromptExactly()
    {
        var rig = Rig();
        var findings = new FakeFindingStore();
        var request = Guid.NewGuid();

        var report = await Analyzer(rig, rig.Sink, findings).AnalyzeAsync(request, Assessment(), CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result);
        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelRole.CandidateAmbiguityAssessment, write.Snapshot.Role);
        Assert.AreEqual("provider-ref-1", write.Snapshot.SubjectReference, "Same convention as the finding's subject.");
        Assert.AreEqual(request, write.Snapshot.RequestId);
        Assert.IsNull(write.Snapshot.StaleReason);
        Assert.AreEqual("Uncertain", write.Call.Answer);
        Assert.AreEqual("provider/ambig-4b", write.Call.ReportedModel);
        Assert.AreEqual(findings.Findings.Single().Id, write.Call.SemanticFindingId);
        var rebuilt = TuningCaseRenderer.Render(ModelRole.CandidateAmbiguityAssessment,
            TuningInputJson.Read(ModelRole.CandidateAmbiguityAssessment, write.Snapshot.SnapshotJson), TuningSource.Household, TuningCoverage.ContractOnly);
        var sent = rig.Model.Calls.Single().Request.Messages;
        for (var i = 0; i < sent.Count; i++) Assert.AreEqual(sent[i].Content, rebuilt.Messages[i].Content, "Round trip reproduces the prompt byte for byte.");
    }

    [TestMethod]
    public async Task TheSnapshotHoldsNoProviderReferenceTokenRevisionOrLinkAndTheLinkCarriesTheReference()
    {
        var rig = Rig();

        await Analyzer(rig, rig.Sink).AnalyzeAsync(null, Assessment(), CancellationToken.None);

        var write = rig.Writer.Writes.Single();
        foreach (var forbidden in new[] { CaptureData.SecretToken, CaptureData.SecretRevision, "provider-ref-", "https://", "example.test", "origin summary", "asin", "B000TEST" })
            Assert.IsFalse(write.Snapshot.SnapshotJson.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Snapshot JSON must not contain '{forbidden}'.");
        Assert.AreEqual("provider-ref-1", write.Links.Single().ProviderResultId);
        Assert.AreEqual("c1", write.Links.Single().CandidateKey);
    }

    [TestMethod]
    public async Task ASinkThatThrowsOrCaptureOffLeavesTheReportAndFindingUnchanged()
    {
        var baselineRig = Rig(capture: false);
        var baselineFindings = new FakeFindingStore();
        var baseline = await Analyzer(baselineRig, baselineRig.Sink, baselineFindings).AnalyzeAsync(null, Assessment(), CancellationToken.None);
        Assert.IsEmpty(baselineRig.Writer.Writes, "Capture off writes nothing.");
        var rig = Rig();
        var findings = new FakeFindingStore();
        var sink = new ThrowingSink();

        var report = await Analyzer(rig, sink, findings).AnalyzeAsync(null, Assessment(), CancellationToken.None);

        Assert.AreEqual(1, sink.Calls);
        Assert.AreEqual(baseline.Result, report.Result);
        Assert.AreEqual(baselineFindings.Findings.Single().Verdict, findings.Findings.Single().Verdict);
        Assert.AreEqual(baselineFindings.Findings.Single().ClaimsJson, findings.Findings.Single().ClaimsJson);
        Assert.AreEqual(baselineRig.Model.Calls.Count, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ASettledCandidateAReusedFindingAndARefusedCallSaveNothing()
    {
        var rig = Rig();
        var analyzer = Analyzer(rig, rig.Sink);
        var assessment = Assessment();
        await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);
        var reused = await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);
        Assert.AreEqual(SemanticAnalysisResult.Reused, reused.Result);
        Assert.HasCount(1, rig.Writer.Writes, "The reused question made no call, so there is nothing new to record.");

        var settled = CaptureData.Option(CaptureData.Requested, 2, "A Different Book", "Nobody Known", "A.Different.Book").IdentityAssessment!;
        Assert.IsFalse(CandidateSemanticEvidence.IsEligible(settled), "A hard mismatch is never sent to a model.");
        var notEligible = await Analyzer(rig, rig.Sink).AnalyzeAsync(null, settled, CancellationToken.None);
        Assert.AreEqual(SemanticAnalysisResult.NotEligible, notEligible.Result);
        Assert.HasCount(1, rig.Writer.Writes);

        var off = new CaptureRig(ModelRole.CandidateAmbiguityAssessment, (_, _) => ScriptedModel.Ok(Uncertain), enableRole: false);
        var refused = await Analyzer(off, off.Sink).AnalyzeAsync(null, Assessment(), CancellationToken.None);
        Assert.AreEqual(SemanticAnalysisResult.Refused, refused.Result);
        Assert.IsEmpty(off.Writer.Writes);
    }
}
