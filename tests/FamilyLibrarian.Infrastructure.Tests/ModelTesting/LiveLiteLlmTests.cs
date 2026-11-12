using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelTesting;
using FamilyLibrarian.Infrastructure.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTesting;

/// <summary>
/// OPT-IN live checks against a real LiteLLM endpoint. They never run in ordinary CI: without the variables below every test
/// reports "inconclusive" and the build needs no credentials, network or cloud access.
/// <list type="bullet">
/// <item><c>FAMILY_LIBRARIAN_LIVE_LITELLM=1</c> switches the tests on.</item>
/// <item><c>FAMILY_LIBRARIAN_LITELLM_URL</c>, <c>FAMILY_LIBRARIAN_LITELLM_KEY</c> (optional) and <c>FAMILY_LIBRARIAN_LITELLM_MODEL</c> name the endpoint, key and primary alias.</item>
/// <item><c>FAMILY_LIBRARIAN_LITELLM_COMPARISON_MODEL</c> optionally names a second local alias.</item>
/// <item><c>FAMILY_LIBRARIAN_LITELLM_CLOUD_MODEL</c> together with <c>FAMILY_LIBRARIAN_LITELLM_ALLOW_CLOUD=1</c> enables the cloud test; without both, no cloud request is ever made.</item>
/// </list>
/// A passing run is the only thing that may be recorded as "live connectivity verified".
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class LiveLiteLlmTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable($"FAMILY_LIBRARIAN_{name}");

    private static bool LiveEnabled => Env("LIVE_LITELLM") == "1" && Env("LITELLM_URL") is { Length: > 0 } && Env("LITELLM_MODEL") is { Length: > 0 };

    private sealed class DefaultFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    private static ModelCapabilityTestService Create(out FakeSettingsStore store, bool allowCloud)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new ModelTestSettings(now);
        settings.Configure(
            Env("LITELLM_MODEL"), Env("LITELLM_COMPARISON_MODEL"), Env("LITELLM_CLOUD_MODEL"), allowCloud,
            timeoutSeconds: 90, maxOutputTokens: 1024, actorUserId: null, now);
        settings.SetEnabled(true, null, now);
        store = new FakeSettingsStore { Settings = settings };
        var targets = new FakeTargetSource
        {
            Connection = new ModelTestConnectionInfo(new Uri(Env("LITELLM_URL")!), Env("LITELLM_KEY") is { Length: > 0 } key ? key : null, false, null)
        };
        return new ModelCapabilityTestService(
            store, targets, new OpenAiCompatibleModelClient(new DefaultFactory()), new ModelTestGate(),
            new CapturingAudit(), new FakeUser(Guid.NewGuid()), new FakeAdmin(true));
    }

    private static void Report(TestContext context, ModelTestReport report)
    {
        context.WriteLine($"Model {report.ConfiguredModel} (reported: {string.Join(", ", report.ReportedModels)}), cloud={report.IsCloud}, calls={report.TotalCalls}, tokens={report.TotalTokens}, ms={report.TotalLatencyMilliseconds}");
        foreach (var result in report.Results)
        {
            context.WriteLine($"  {result.Check,-22} {result.Status,-12} calls={result.Calls} ms={result.LatencyMilliseconds} tokens={result.TotalTokens} :: {result.Detail}");
        }
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ThePrimaryLocalModelAnswersThroughLiteLlm()
    {
        if (!LiveEnabled) Assert.Inconclusive("Set FAMILY_LIBRARIAN_LIVE_LITELLM=1 and the LITELLM_URL / LITELLM_MODEL variables to run live tests.");
        var service = Create(out _, allowCloud: false);

        var result = await service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Primary, null, false), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Completed, result.Outcome, result.Error);
        Report(TestContext, result.Report!);
        Assert.AreEqual(ModelCheckStatus.Passed, result.Report!.Results.Single(item => item.Check == ModelCheck.Connectivity).Status,
            "A local model must answer through LiteLLM for connectivity to be verified.");
        Assert.AreEqual(ModelCheckStatus.Passed, result.Report.Results.Single(item => item.Check == ModelCheck.MalformedResponses).Status);
        Assert.IsTrue(result.Report.Results.All(item => item.Status != ModelCheckStatus.Error || item.Check == ModelCheck.ModelUnavailable),
            "Capability gaps are reported (Failed / NotSupported); only an unclassified error would be a harness problem.");
    }

    [TestMethod]
    public async Task TheComparisonLocalModelIsReportedSeparately()
    {
        if (!LiveEnabled || Env("LITELLM_COMPARISON_MODEL") is not { Length: > 0 })
        {
            Assert.Inconclusive("Needs the live variables plus FAMILY_LIBRARIAN_LITELLM_COMPARISON_MODEL.");
        }

        var service = Create(out _, allowCloud: false);

        var result = await service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Comparison, null, false), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Completed, result.Outcome, result.Error);
        Report(TestContext, result.Report!);
        Assert.AreEqual(Env("LITELLM_COMPARISON_MODEL"), result.Report!.ConfiguredModel);
    }

    [TestMethod]
    public async Task TheCloudModelIsCalledOnlyWithBothTheSettingAndTheExplicitConfirmation()
    {
        if (!LiveEnabled || Env("LITELLM_CLOUD_MODEL") is not { Length: > 0 } || Env("LITELLM_ALLOW_CLOUD") != "1")
        {
            Assert.Inconclusive("Needs the live variables plus FAMILY_LIBRARIAN_LITELLM_CLOUD_MODEL and FAMILY_LIBRARIAN_LITELLM_ALLOW_CLOUD=1 (this makes a paid or rate-limited call).");
        }

        var service = Create(out _, allowCloud: true);

        var unconfirmed = await service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Cloud, [ModelCheck.Connectivity], false), CancellationToken.None);
        var confirmed = await service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Cloud, [ModelCheck.Connectivity], true), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.CloudNotPermitted, unconfirmed.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Completed, confirmed.Outcome);
        Report(TestContext, confirmed.Report!);
    }
}
