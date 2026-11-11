using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-b, candidate triage: the shown candidates, the pick and the ties are saved after the move is decided, exactly.</summary>
[TestClass]
public sealed class TriageCaptureTests
{
    private const string Keep = "{\"schema\":\"candidate-triage/1\",\"move\":\"keep\",\"candidate\":null,\"evidence_ids\":[],\"note\":\"Nothing supports a move.\"}";
    private static readonly BookIdentity Requested = CaptureData.Requested;
    private static readonly string[] TiedKeys = ["c1", "c2"];

    private static CaptureRig Rig(string reply = Keep, bool capture = true) =>
        new(ModelRole.CandidateTriage, (_, _) => ScriptedModel.Ok(reply, model: "provider/triage-9b"), ModelReasoningMode.Disabled, capture);

    private static CandidateTriageService Service(CaptureRig rig, IDecisionCaptureSink? sink, FakeFindingStore? findings = null) =>
        new(rig.Resolver, rig.Policy, findings ?? new FakeFindingStore(), rig.Clock, null, sink);

    private static CandidateTriageContext Context(int count = 3, int pickIndex = 1, params int[] tiedIndexes)
    {
        var options = Enumerable.Range(1, count).Select(n => CaptureData.Option(Requested, n, n == 1 ? "The Lantern Road" : $"Another Book {n}", "Mara Quill", $"Release.{n}")).ToArray();
        return new CandidateTriageContext(Requested, options, pickIndex > 0 ? options[pickIndex - 1] : null,
            [.. tiedIndexes.Select(i => options[i - 1].ProviderResultId)], ProviderConfidenceLevel.Standard);
    }

    private static Task<CandidateTriageResult?> Run(CandidateTriageService service, CandidateTriageContext context, Guid? format = null, Guid? request = null) =>
        service.GetAsync(request ?? Guid.NewGuid(), format ?? Guid.NewGuid(), "example-source", context, CancellationToken.None);

    [TestMethod]
    public async Task APickWithATieIsSavedWithLocalKeysAndReproducesThePromptExactly()
    {
        var rig = Rig();
        var findings = new FakeFindingStore();
        var request = Guid.NewGuid();
        var format = Guid.NewGuid();

        await Run(Service(rig, rig.Sink, findings), Context(3, 1, 1, 2), format, request);

        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelRole.CandidateTriage, write.Snapshot.Role);
        Assert.AreEqual(format.ToString("N") + ":example-source", write.Snapshot.SubjectReference);
        Assert.AreEqual(request, write.Snapshot.RequestId);
        Assert.IsNull(write.Snapshot.StaleReason, "Rebuilding the snapshot reproduces the exact prompt that was sent.");
        Assert.AreEqual("keep", write.Call.Answer);
        Assert.AreEqual("provider/triage-9b", write.Call.ReportedModel);
        Assert.AreEqual(ModelReasoningMode.Disabled, write.Call.Reasoning);
        Assert.AreEqual(findings.Findings.Single().Id, write.Call.SemanticFindingId);
        var input = (CandidateTriageCaseInput)TuningInputJson.Read(ModelRole.CandidateTriage, write.Snapshot.SnapshotJson);
        Assert.AreEqual("c1", input.PickKey);
        CollectionAssert.AreEqual(TiedKeys, input.TiedKeys.ToArray());
        Assert.HasCount(3, input.Candidates);
        Assert.AreEqual(ProviderConfidenceLevel.Standard, input.Confidence);
        Assert.AreEqual("result-2", write.Links.Single(link => link.CandidateKey == "c2").ProviderResultId);
    }

    [TestMethod]
    public async Task TheSnapshotRebuildsIntoTheExactMessagesThatWereSent()
    {
        var rig = Rig();
        await Run(Service(rig, rig.Sink), Context(3, 1, 1, 2));
        var stored = rig.Writer.Writes.Single().Snapshot;

        var rebuilt = TuningCaseRenderer.Render(ModelRole.CandidateTriage, TuningInputJson.Read(ModelRole.CandidateTriage, stored.SnapshotJson),
            TuningSource.Household, TuningCoverage.ContractOnly);

        var sent = rig.Model.Calls.Single().Request.Messages;
        Assert.HasCount(sent.Count, rebuilt.Messages);
        for (var i = 0; i < sent.Count; i++) Assert.AreEqual(sent[i].Content, rebuilt.Messages[i].Content);
    }

    [TestMethod]
    public async Task ThePickIsKeptFirstEvenWhenItWasNotFirstInTheSourceAndOnlyTheEightShownAreSaved()
    {
        var rig = Rig();
        // Eleven candidates and the pick is the ninth: the prompt moves it to the front and shows eight, so the snapshot does too.
        var context = Context(11, 9);

        await Run(Service(rig, rig.Sink), context);

        var write = rig.Writer.Writes.Single();
        var input = (CandidateTriageCaseInput)TuningInputJson.Read(ModelRole.CandidateTriage, write.Snapshot.SnapshotJson);
        Assert.HasCount(8, input.Candidates);
        Assert.AreEqual("c1", input.PickKey);
        Assert.AreEqual("result-9", write.Links.Single(link => link.CandidateKey == "c1").ProviderResultId, "The pick leads, as in the prompt.");
        Assert.IsNull(write.Snapshot.StaleReason);
        Assert.HasCount(8, write.Links);
    }

    [TestMethod]
    public async Task TheSnapshotHoldsNoProviderIdsTokensRevisionsOrLinks()
    {
        var rig = Rig();
        await Run(Service(rig, rig.Sink), Context(3, 1, 1, 2));
        var json = rig.Writer.Writes.Single().Snapshot.SnapshotJson;

        foreach (var forbidden in new[]
        {
            CaptureData.SecretToken, CaptureData.SecretRevision, "provider-data-SECRET", "example-source", "result-", "provider-ref-",
            "https://", "example.test", "origin summary", "asin", "B000TEST", "acquireToken", "candidateRevision", "providerId"
        })
            Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Snapshot JSON must not contain '{forbidden}'.");
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(3, document.RootElement.GetProperty("candidates").GetArrayLength());
    }

    [TestMethod]
    public async Task AReleaseNameWithAFileExtensionIsNeverStoredAndTheDecisionIsStillSaved()
    {
        var rig = Rig();
        var named = CaptureData.Option(Requested, 1, "The Lantern Road", "Mara Quill", "The.Lantern.Road.Mara.Quill.epub");
        var context = new CandidateTriageContext(Requested, [named], named, [], ProviderConfidenceLevel.Standard);

        await Run(Service(rig, rig.Sink), context);

        var write = rig.Writer.Writes.Single();
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains(".epub", StringComparison.OrdinalIgnoreCase), "A file-name-like release name is withheld.");
        // Whether the snapshot is still exact depends on whether the prompt used the name; the round trip decides, never this test.
        Assert.IsTrue(write.Snapshot.StaleReason is null or DecisionCaptureRecorder.StaleRenderMismatch);
    }

    [TestMethod]
    public async Task ASinkThatThrowsLeavesTheMoveAndTheFindingUnchanged()
    {
        var without = Rig();
        var withoutFindings = new FakeFindingStore();
        var baseline = await Run(Service(without, null, withoutFindings), Context(3, 1, 1, 2));
        var rig = Rig();
        var findings = new FakeFindingStore();
        var sink = new ThrowingSink();

        var result = await Run(Service(rig, sink, findings), Context(3, 1, 1, 2));

        Assert.AreEqual(1, sink.Calls);
        Assert.AreEqual(baseline?.Move, result?.Move);
        Assert.AreEqual(withoutFindings.Findings.Single().Verdict, findings.Findings.Single().Verdict);
        Assert.AreEqual(withoutFindings.Findings.Single().ClaimsJson, findings.Findings.Single().ClaimsJson);
        Assert.AreEqual(without.Model.Calls.Count, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task CaptureOffWritesNothingAndAReusedFindingMakesNoCallAndSavesNothingMore()
    {
        var off = Rig(capture: false);
        await Run(Service(off, off.Sink), Context(3, 1, 1, 2));
        Assert.IsEmpty(off.Writer.Writes);

        var rig = Rig();
        var service = Service(rig, rig.Sink);
        var request = Guid.NewGuid();
        var format = Guid.NewGuid();
        await Run(service, Context(3, 1, 1, 2), format, request);
        await Run(service, Context(3, 1, 1, 2), format, request);

        Assert.AreEqual(1, rig.Model.Calls.Count, "The second identical question reused the finding.");
        Assert.HasCount(1, rig.Writer.Writes);
    }

    [TestMethod]
    public async Task ARejectedMoveIsStillKeptWithItsOutcome()
    {
        var rig = Rig(reply: "{\"schema\":\"candidate-triage/1\",\"move\":\"prefer\",\"candidate\":\"c1\",\"evidence_ids\":[],\"note\":\"x\"}");

        await Run(Service(rig, rig.Sink), Context(3, 1, 1, 2));

        var write = rig.Writer.Writes.Single();
        Assert.AreNotEqual(Domain.ModelGateway.ModelCallOutcome.Succeeded, write.Call.Outcome);
        Assert.IsNull(write.Call.Answer);
    }
}
