using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

/// <summary>
/// Opt-in live check of the PRODUCTION shadow analyzers (candidate and artifact) through the real
/// <see cref="ModelExecutionPolicy"/> and the real HTTP model client against a LiteLLM endpoint, using only fictional
/// evidence held in memory: no lab database, no file, nothing that could be approved or published. Skipped unless
/// <c>FAMILY_LIBRARIAN_LIVE_LITELLM=1</c> and the LITELLM variables are set. Only a local alias is ever used.
/// Output records outcome, latency and token counts, never prompts or replies.
/// </summary>
[TestClass]
public sealed class LiveSemanticShadowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static string? Env(string name) => Environment.GetEnvironmentVariable($"FAMILY_LIBRARIAN_{name}");

    private static bool Enabled => Env("LIVE_LITELLM") == "1" && Env("LITELLM_URL") is { Length: > 0 } && Env("LITELLM_MODEL") is { Length: > 0 };

    public TestContext TestContext { get; set; } = null!;

    private sealed class DefaultFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    private static ModelGatewaySettings Settings(ModelRole role, ModelReasoningMode reasoning)
    {
        var settings = new ModelGatewaySettings(Now);
        settings.Configure(Env("LITELLM_URL"), allowCloud: false, null, Now);
        if (Env("LITELLM_KEY") is { Length: > 0 } key) settings.SetApiKey($"p:{key}", 1, null, Now);
        settings.ConfigureRole(role, true, ModelPlacement.Local, Env("LITELLM_MODEL"), null, null, Now, reasoning);
        settings.SetEnabled(true, null, Now);
        return settings;
    }

    private static (ModelExecutionPolicy Policy, IModelRoleResolver Resolver, FakeLedger Ledger) Pipeline(ModelGatewaySettings settings)
    {
        var resolver = new ModelRoleResolver(new FakeGatewayStore { Settings = settings }, new PassThroughProtector());
        var ledger = new FakeLedger();
        var policy = new ModelExecutionPolicy(
            resolver, new OpenAiCompatibleModelClient(new DefaultFactory()), ledger, new ModelExecutionGate(), new ModelCircuitBreaker(), new MutableClock(Now));
        return (policy, resolver, ledger);
    }

    private void Report(string what, ModelReasoningMode reasoning, FakeLedger ledger, FakeFindingStore store)
    {
        var record = ledger.Records.Single();
        var finding = store.Findings.Single();
        TestContext.WriteLine(
            $"{what}: model={record.Model} reasoning={reasoning} outcome={finding.Outcome} verdict={finding.Verdict ?? "-"} " +
            $"claims={(finding.ClaimsJson is null ? 0 : JsonDocument.Parse(finding.ClaimsJson).RootElement.GetArrayLength())} " +
            $"latency_ms={record.LatencyMilliseconds} tokens(prompt/completion/total)={record.ReportedPromptTokens}/{record.ReportedCompletionTokens}/{record.ReportedTotalTokens} charged={record.ChargedTokens} estimated={record.UsageEstimated}");
    }

    // ---- candidate (fictional ambiguous release) ----

    private static readonly BookIdentity Requested = new("The Lantern Road", "Mara Quill", [], Language: "en");

    private static CandidateIdentityAssessment AmbiguousCandidate() =>
        DeterministicCandidateIdentityResolver.Assess(Requested, new ExternalProviderCandidate(
            "ref-ambiguous", new ExternalProviderWorkEvidence(string.Empty, null, [], [], []), null,
            new ExternalProviderReleaseEvidence("Mara Quill complete collection epub", null, null, null, null, null, null, null, [], null)));

    [TestMethod]
    [DataRow(ModelReasoningMode.Inherit)]
    [DataRow(ModelReasoningMode.Disabled)]
    public async Task TheProductionCandidateAnalyzerRunsThroughTheRealPolicyWithoutChangingTheDeterministicResult(ModelReasoningMode reasoning)
    {
        if (!Enabled) Assert.Inconclusive("Set FAMILY_LIBRARIAN_LIVE_LITELLM=1 and the LITELLM_URL / LITELLM_MODEL (/ LITELLM_KEY) variables to run live tests.");
        var (policy, _, ledger) = Pipeline(Settings(ModelRole.CandidateAmbiguityAssessment, reasoning));
        var store = new FakeFindingStore();
        var analyzer = new CandidateSemanticAnalyzer(policy, store, new MutableClock(Now));
        var assessment = AmbiguousCandidate();
        var before = JsonSerializer.Serialize(assessment);

        var report = await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result);
        Assert.AreEqual(before, JsonSerializer.Serialize(assessment), "The deterministic assessment is untouched.");
        Assert.AreEqual(WorkIdentityDecision.Ambiguous, assessment.Decision);
        Assert.AreEqual(ModelBudgetClass.BackgroundValidation, ledger.Records.Single().BudgetClass);
        Assert.AreEqual(1, store.Findings.Count, "Success or failure, exactly one finding beside the decision.");
        Report("candidate", reasoning, ledger, store);
        if (reasoning == ModelReasoningMode.Disabled)
        {
            Assert.AreNotEqual(ModelCallOutcome.OutputLimitReached, store.Findings.Single().Outcome, "Reasoning off should leave room for the answer.");
            Assert.AreNotEqual(ModelCallOutcome.EmptyContent, store.Findings.Single().Outcome);
        }
    }

    // ---- artifact (fictional recorded evidence) ----

    private static readonly Guid WorkId = Guid.NewGuid();

    private sealed class FakeAssets(MediaAsset asset) : ISecurityEvaluationRepository
    {
        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(asset.Id == assetId ? asset : null);

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeEvidence(IReadOnlyList<ArtifactEvidence> items) : IArtifactEvidenceStore
    {
        public void Add(IReadOnlyList<ArtifactEvidence> evidence) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
            IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(
                items.GroupBy(item => item.MediaAssetId).ToDictionary(group => group.Key, group => (IReadOnlyList<ArtifactEvidence>)group.ToArray()));
    }

    private sealed class FakeWorks : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Lantern Road", "Mara Quill", [], Language: "en"));
    }

    [TestMethod]
    [DataRow(ModelReasoningMode.Inherit)]
    [DataRow(ModelReasoningMode.Disabled)]
    public async Task TheProductionArtifactAnalyzerRunsThroughTheRealPolicyAndLeavesTheAssetAndEvidenceUntouched(ModelReasoningMode reasoning)
    {
        if (!Enabled) Assert.Inconclusive("Set FAMILY_LIBRARIAN_LIVE_LITELLM=1 and the LITELLM_URL / LITELLM_MODEL (/ LITELLM_KEY) variables to run live tests.");
        var asset = new MediaAsset(
            WorkId, null, RequestMediaType.Ebook, "epub", "lantern.epub", "stored-lantern", 1000, "abc123", "application/epub+zip", Guid.NewGuid(), null, Now);
        ArtifactEvidence Item(string id, EvidenceDimension dimension, string code, EvidencePolarity polarity, string? value, string kind = ArtifactEvidenceRecordKinds.IdentityVerification) =>
            new(asset.Id, asset.Sha256, kind, Guid.Parse("11111111-1111-1111-1111-111111111111"), id, dimension, code, polarity, "verifier", value, "1", Now);
        IReadOnlyList<ArtifactEvidence> evidence =
        [
            Item("ev-title", EvidenceDimension.IdentityCorroboration, "artifact.identity.title_unknown", EvidencePolarity.Unknown, "Les Lanternes"),
            Item("ev-language", EvidenceDimension.Language, "artifact.language.matches", EvidencePolarity.Positive, "en"),
            Item("ev-scan", EvidenceDimension.Security, "artifact.security.clean", EvidencePolarity.Positive, null, ArtifactEvidenceRecordKinds.SecurityEvaluation)
        ];
        var (policy, resolver, ledger) = Pipeline(Settings(ModelRole.ArtifactValidationAssessment, reasoning));
        var store = new FakeFindingStore();
        var analyzer = new ArtifactSemanticAnalyzer(policy, resolver, store, new FakeAssets(asset), new FakeEvidence(evidence), new FakeWorks(), new MutableClock(Now));
        var stateBefore = asset.StorageState;
        var evidenceBefore = JsonSerializer.Serialize(evidence.Select(item => (item.FindingId, item.Code, item.Polarity, item.Value)));

        var report = await analyzer.AnalyzeAsync(asset.Id, CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result);
        Assert.AreEqual(stateBefore, asset.StorageState, "Analysis cannot change the asset.");
        Assert.AreEqual(evidenceBefore, JsonSerializer.Serialize(evidence.Select(item => (item.FindingId, item.Code, item.Polarity, item.Value))));
        Assert.AreEqual(1, store.Findings.Count);
        Report("artifact", reasoning, ledger, store);
        if (reasoning == ModelReasoningMode.Disabled)
        {
            Assert.AreNotEqual(ModelCallOutcome.OutputLimitReached, store.Findings.Single().Outcome);
            Assert.AreNotEqual(ModelCallOutcome.EmptyContent, store.Findings.Single().Outcome);
        }
    }
}
