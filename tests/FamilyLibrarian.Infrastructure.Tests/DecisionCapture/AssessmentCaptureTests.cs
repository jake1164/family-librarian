using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-b, file assessment: recorded evidence only, never the file, its names or its hashes; values only when the model saw them.</summary>
[TestClass]
public sealed class AssessmentCaptureTests
{
    private const string Uncertain = "{\"schema\":\"artifact-assessment/1\",\"verdict\":\"uncertain\",\"claims\":[{\"kind\":\"insufficient_information\",\"evidence_ids\":[],\"note\":\"Not enough evidence.\"}]}";
    private const string OriginalFilename = "my-private-lantern-file.epub";
    private const string StoredName = "stored-secret-location";
    private const string Hash = "9f2b4c1d8e7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c";
    private static readonly DateTimeOffset Now = CaptureRig.Now;

    private sealed class Assets(MediaAsset asset) : ISecurityEvaluationRepository
    {
        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult<MediaAsset?>(asset.Id == assetId ? asset : null);

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Evidence(IReadOnlyList<ArtifactEvidence> items) : IArtifactEvidenceStore
    {
        public void Add(IReadOnlyList<ArtifactEvidence> evidence) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(
                items.GroupBy(item => item.MediaAssetId).ToDictionary(group => group.Key, group => (IReadOnlyList<ArtifactEvidence>)group.ToArray()));
    }

    private sealed class Works : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Lantern Road", "Mara Quill", [], Language: "en"));
    }

    private static MediaAsset NewAsset() => new(
        Guid.NewGuid(), null, RequestMediaType.Ebook, "epub", OriginalFilename, StoredName, 1000, Hash, "application/epub+zip", Guid.NewGuid(), null, Now);

    private static ArtifactEvidence Row(MediaAsset asset, string id, EvidenceDimension dimension, string code, EvidencePolarity polarity, string? value,
        string kind = ArtifactEvidenceRecordKinds.IdentityVerification) =>
        new(asset.Id, asset.Sha256, kind, Guid.NewGuid(), id, dimension, code, polarity, "verifier", value, "1", Now);

    private static IReadOnlyList<ArtifactEvidence> Unsettled(MediaAsset asset, string title = "Les Lanternes") =>
    [
        Row(asset, "ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAbsent, EvidencePolarity.Unknown, title),
        Row(asset, "ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.DeclaredMatchesRequest, EvidencePolarity.Positive, "en"),
        Row(asset, "ev-scan", EvidenceDimension.Security, EvidenceReasonCodes.ArtifactSecurity.ScanClean, EvidencePolarity.Positive, null, ArtifactEvidenceRecordKinds.SecurityEvaluation)
    ];

    private static (CaptureRig Rig, ArtifactSemanticAnalyzer Analyzer, FakeFindingStore Findings, MediaAsset Asset) Setup(
        Func<MediaAsset, IReadOnlyList<ArtifactEvidence>> evidence, bool cloud = false, bool excerpts = false, bool capture = true,
        IDecisionCaptureSink? sinkOverride = null, bool enableRole = true)
    {
        var asset = NewAsset();
        var rig = new CaptureRig(ModelRole.ArtifactValidationAssessment, (_, _) => ScriptedModel.Ok(Uncertain, model: "provider/assess-8b"),
            ModelReasoningMode.Disabled, capture, cloud: cloud, allowCloudExcerpts: excerpts, enableRole: enableRole);
        var findings = new FakeFindingStore();
        var analyzer = new ArtifactSemanticAnalyzer(rig.Policy, rig.Resolver, findings, new Assets(asset), new Evidence(evidence(asset)), new Works(),
            rig.Clock, null, sinkOverride ?? rig.Sink);
        return (rig, analyzer, findings, asset);
    }

    [TestMethod]
    public async Task ALocalAssessmentIsSavedWithItsEvidenceReproducesThePromptExactlyAndLinksTheAssetWithoutAnyFileIdentity()
    {
        var (rig, analyzer, findings, asset) = Setup(a => Unsettled(a));

        var report = await analyzer.AnalyzeAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result);
        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelRole.ArtifactValidationAssessment, write.Snapshot.Role);
        Assert.AreEqual(asset.Id.ToString("N"), write.Snapshot.SubjectReference);
        Assert.AreEqual(asset.Id, write.Snapshot.MediaAssetId);
        Assert.IsNull(write.Snapshot.StaleReason, "The rebuilt prompt reproduces the one that was sent.");
        Assert.IsFalse(write.Snapshot.ExcerptsInvolved);
        Assert.AreEqual("Uncertain", write.Call.Answer);
        Assert.AreEqual(findings.Findings.Single().Id, write.Call.SemanticFindingId);
        StringAssert.Contains(write.Snapshot.SnapshotJson, "Les Lanternes");
        foreach (var forbidden in new[] { OriginalFilename, "my-private", StoredName, Hash, "application/epub" })
            Assert.IsFalse(write.Snapshot.SnapshotJson.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Snapshot JSON must not contain '{forbidden}'.");
        var rebuilt = TuningCaseRenderer.Render(ModelRole.ArtifactValidationAssessment,
            TuningInputJson.Read(ModelRole.ArtifactValidationAssessment, write.Snapshot.SnapshotJson), TuningSource.Household, TuningCoverage.ContractOnly);
        var sent = rig.Model.Calls.Single().Request.Messages;
        for (var i = 0; i < sent.Count; i++) Assert.AreEqual(sent[i].Content, rebuilt.Messages[i].Content);
    }

    [TestMethod]
    public async Task ValuesTheModelWasNotAllowedToSeeAreNotSavedEither()
    {
        // A cloud placement without the separate excerpt opt-in is shown no file-derived values, so none may be stored.
        var (rig, analyzer, _, asset) = Setup(a => Unsettled(a), cloud: true, excerpts: false);

        await analyzer.AnalyzeAsync(asset.Id, CancellationToken.None);

        var write = rig.Writer.Writes.Single();
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("Les Lanternes", StringComparison.Ordinal), "A withheld value is not stored.");
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("\"value\":\"en\"", StringComparison.Ordinal));
        Assert.AreEqual(ModelPlacement.Cloud, write.Call.Placement);
        Assert.IsNull(write.Snapshot.StaleReason, "The prompt had no values either, so the rebuild still matches.");
        StringAssert.Contains(write.Snapshot.SnapshotJson, "\"includeDetails\":false");
    }

    [TestMethod]
    public async Task ProfileFindingsAreNeverStoredBecauseThePromptNeverShowsThem()
    {
        var (rig, analyzer, _, asset) = Setup(a => [.. Unsettled(a), Row(a, "ev-toc-1", EvidenceDimension.Profile, EvidenceReasonCodes.ArtifactProfile.PackagePublisher, EvidencePolarity.Unknown, "Chapter One of the private book")]);

        await analyzer.AnalyzeAsync(asset.Id, CancellationToken.None);

        var write = rig.Writer.Writes.Single();
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("Chapter One", StringComparison.Ordinal));
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("profile", StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(write.Snapshot.StaleReason);
    }

    [TestMethod]
    [DataRow("See https://evil.example.test/page for the real title")]
    [DataRow("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive")]
    public async Task AValueTheAllowListRefusesIsWithheldAndTheSnapshotIsReportedNonExactNotDropped(string value)
    {
        var (rig, analyzer, _, asset) = Setup(a => Unsettled(a, value));

        var report = await analyzer.AnalyzeAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result);
        var write = rig.Writer.Writes.Single();
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("evil.example", StringComparison.Ordinal));
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains("twentyfive", StringComparison.Ordinal));
        Assert.AreEqual(DecisionCaptureRecorder.StaleRenderMismatch, write.Snapshot.StaleReason, "The prompt showed the value; the snapshot cannot, so it says so.");
    }

    [TestMethod]
    public async Task ASinkThatThrowsOrCaptureOffOrARefusedRoleChangeNothing()
    {
        var (offRig, offAnalyzer, offFindings, offAsset) = Setup(a => Unsettled(a), capture: false);
        var baseline = await offAnalyzer.AnalyzeAsync(offAsset.Id, CancellationToken.None);
        Assert.IsEmpty(offRig.Writer.Writes);

        var sink = new ThrowingSink();
        var (rig, analyzer, findings, asset) = Setup(a => Unsettled(a), sinkOverride: sink);
        var report = await analyzer.AnalyzeAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(1, sink.Calls);
        Assert.AreEqual(baseline.Result, report.Result);
        Assert.AreEqual(offFindings.Findings.Single().Verdict, findings.Findings.Single().Verdict);
        Assert.AreEqual(offRig.Model.Calls.Count, rig.Model.Calls.Count);

        var (refusedRig, refusedAnalyzer, _, refusedAsset) = Setup(a => Unsettled(a), enableRole: false);
        Assert.AreEqual(SemanticAnalysisResult.Refused, (await refusedAnalyzer.AnalyzeAsync(refusedAsset.Id, CancellationToken.None)).Result);
        Assert.IsEmpty(refusedRig.Writer.Writes);
    }
}
