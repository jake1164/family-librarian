using System.Text.Json;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-b, hold decision: the facts the reviewer saw are saved without book text; a call that carried excerpts says so.</summary>
[TestClass]
public sealed class HoldCaptureTests
{
    private const string Prose = "The silver orchard beyond the silent river conceals our fictional secret.";

    private static AssetIdentityVerificationResult Verification() => AssetIdentityVerificationResult.Match("test",
    [
        new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
        new("ev-concern", EvidenceDimension.Profile, EvidenceReasonCodes.ProfileComparison.LengthBelowExpected, EvidencePolarity.Weak, "words=12000;pages=100")
    ]);

    private static string Reply(string verdict = "hold") => ArtifactDecisionKit.Reply(verdict);

    private static CaptureRig Rig(string? reply = null, bool capture = true, bool cloud = false, bool excerptsAllowed = false) =>
        new(ModelRole.ArtifactValidationDecision, (_, _) => ScriptedModel.Ok(reply ?? Reply(), model: "provider/hold-8b"), ModelReasoningMode.Disabled, capture,
            cloud: cloud, allowCloudExcerpts: excerptsAllowed);

    private static ArtifactDecisionService Service(CaptureRig rig, IDecisionCaptureSink? sink, FakeFindingStore? findings = null,
        IArtifactExcerptExtractor? extractor = null) =>
        new(rig.Resolver, rig.Policy, findings ?? new FakeFindingStore(), rig.Clock, excerpts: extractor, capture: sink);

    [TestMethod]
    public async Task AHoldReviewIsSavedWithItsFactsAndReproducesThePromptExactly()
    {
        var rig = Rig();
        var findings = new FakeFindingStore();
        var asset = ArtifactDecisionKit.Asset();

        var hold = await Service(rig, rig.Sink, findings).GetHoldAsync(asset, Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);

        Assert.IsNotNull(hold, "The decision itself is unchanged: this reply holds.");
        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelRole.ArtifactValidationDecision, write.Snapshot.Role);
        Assert.AreEqual(asset.Id.ToString("N"), write.Snapshot.SubjectReference);
        Assert.AreEqual(asset.Id, write.Snapshot.MediaAssetId);
        Assert.IsFalse(write.Snapshot.ExcerptsInvolved);
        Assert.IsNull(write.Snapshot.StaleReason, "No excerpts were involved and the rebuilt prompt reproduces the one that was sent.");
        Assert.AreEqual("hold", write.Call.Answer);
        Assert.AreEqual(findings.Findings.Single().Id, write.Call.SemanticFindingId);
        foreach (var forbidden in new[] { new string('a', 64), "Book.epub", "asset.epub", "application/octet-stream" })
            Assert.IsFalse(write.Snapshot.SnapshotJson.Contains(forbidden, StringComparison.Ordinal), $"Snapshot JSON must not contain '{forbidden}'.");
        var rebuilt = TuningCaseRenderer.Render(ModelRole.ArtifactValidationDecision,
            TuningInputJson.Read(ModelRole.ArtifactValidationDecision, write.Snapshot.SnapshotJson), TuningSource.Household, TuningCoverage.ContractOnly);
        var sent = rig.Model.Calls.Single().Request.Messages;
        for (var i = 0; i < sent.Count; i++) Assert.AreEqual(sent[i].Content, rebuilt.Messages[i].Content, "Round trip reproduces the prompt byte for byte.");
    }

    [TestMethod]
    public async Task ACallThatCarriedBookTextNeverStoresItAndIsMarkedAsInvolvingExcerptsAndNotExact()
    {
        var rig = Rig(reply: ArtifactExcerptTests.Reply(["tc-sample-marker", "tc-last"], kind: "suggests_incomplete"));
        var extractor = new ArtifactExcerptTests.Extractor([new("tc-last", "Continue reading - buy the full book")]);

        var hold = await Service(rig, rig.Sink, extractor: extractor).GetHoldAsync(
            ArtifactDecisionKit.Asset(), Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);

        Assert.IsNotNull(hold);
        Assert.AreEqual(1, extractor.Calls);
        StringAssert.Contains(rig.Model.Calls.Single().Request.Messages[1].Content, "Continue reading - buy the full book");
        var write = rig.Writer.Writes.Single();
        Assert.IsTrue(write.Snapshot.ExcerptsInvolved);
        Assert.AreEqual(DecisionCaptureRecorder.StaleExcerptsInvolved, write.Snapshot.StaleReason, "By definition a rebuild without the text cannot claim the original hash.");
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("Continue reading", StringComparison.OrdinalIgnoreCase), "Book text is never stored.");
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("BOOK_TEXT", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(write.Snapshot.SnapshotJson);
        var derived = document.RootElement.GetProperty("derivedExcerptFacts");
        Assert.IsTrue(derived.GetArrayLength() >= 1, "Only the derived fact codes are kept.");
        Assert.IsFalse(derived[0].TryGetProperty("text", out _));
        Assert.AreEqual("tc-sample-marker", derived[0].GetProperty("findingId").GetString());
    }

    [TestMethod]
    public async Task ExcerptTextReachesNoSavedRowAndNoLogLine()
    {
        var rig = Rig(reply: ArtifactExcerptTests.Reply(["tc-sample-marker", "tc-last"]));
        var extractor = new ArtifactExcerptTests.Extractor([new("tc-last", "Continue reading - buy the full book " + Prose)]);

        await Service(rig, rig.Sink, extractor: extractor).GetHoldAsync(ArtifactDecisionKit.Asset(), Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);

        var write = rig.Writer.Writes.Single();
        var everything = JsonSerializer.Serialize(new { write.Snapshot, write.Call, write.Links }) + string.Join('|', rig.Log.Entries);
        Assert.IsFalse(everything.Contains("silver orchard", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(everything.Contains("Continue reading", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ValuesAreNotSavedWhenTheCloudPlacementWasNotAllowedToSeeThem()
    {
        var rig = Rig(cloud: true, excerptsAllowed: false);

        await Service(rig, rig.Sink).GetHoldAsync(ArtifactDecisionKit.Asset(), Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);

        var write = rig.Writer.Writes.Single();
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("words=12000", StringComparison.Ordinal), "A value the model never saw is not stored.");
        StringAssert.Contains(write.Snapshot.SnapshotJson, "\"includeDetails\":false");
        Assert.AreEqual(ModelPlacement.Cloud, write.Call.Placement);
    }

    [TestMethod]
    public async Task ASinkThatThrowsCaptureOffAndAReusedFindingLeaveTheHoldAndFindingUnchanged()
    {
        var offRig = Rig(capture: false);
        var offFindings = new FakeFindingStore();
        var baseline = await Service(offRig, offRig.Sink, offFindings).GetHoldAsync(ArtifactDecisionKit.Asset(), Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);
        Assert.IsEmpty(offRig.Writer.Writes);

        var rig = Rig();
        var findings = new FakeFindingStore();
        var sink = new ThrowingSink();
        var hold = await Service(rig, sink, findings).GetHoldAsync(ArtifactDecisionKit.Asset(), Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);

        Assert.AreEqual(1, sink.Calls);
        Assert.AreEqual(baseline?.Note, hold?.Note);
        Assert.AreEqual(offFindings.Findings.Single().Verdict, findings.Findings.Single().Verdict);
        Assert.AreEqual(offRig.Model.Calls.Count, rig.Model.Calls.Count);

        var reuseRig = Rig();
        var service = Service(reuseRig, reuseRig.Sink);
        var asset = ArtifactDecisionKit.Asset();
        await service.GetHoldAsync(asset, Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);
        await service.GetHoldAsync(asset, Verification(), ArtifactDecisionKit.Expected, CancellationToken.None);
        Assert.AreEqual(1, reuseRig.Model.Calls.Count);
        Assert.HasCount(1, reuseRig.Writer.Writes, "The reused decision made no call, so nothing more is recorded.");
    }

    [TestMethod]
    public async Task AReviewTheSkipRuleNeverSentToTheModelSavesNothing()
    {
        var rig = Rig();
        var noConcern = AssetIdentityVerificationResult.Match("test",
            [new("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive)]);

        var hold = await Service(rig, rig.Sink).GetHoldAsync(ArtifactDecisionKit.Asset(), noConcern, ArtifactDecisionKit.Expected, CancellationToken.None);

        Assert.IsNull(hold);
        Assert.IsEmpty(rig.Model.Calls);
        Assert.IsEmpty(rig.Writer.Writes);
    }
}
