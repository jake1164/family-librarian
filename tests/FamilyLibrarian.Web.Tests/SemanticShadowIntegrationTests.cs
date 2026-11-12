using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M4-3 over the real host and database: off by default, advisory only, stored beside the deterministic result.</summary>
[TestClass]
[DoNotParallelize] // The tests share one database and one gateway settings row.
public sealed class SemanticShadowIntegrationTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private sealed class ScriptedClient(Func<ModelResponse> handler) : IModelClient
    {
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handler());
        }
    }

    private static FamilyLibrarianAppFactory Factory(ScriptedClient client) =>
        new(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IModelClient>();
            services.AddSingleton<IModelClient>(client);
        });

    private static CandidateIdentityAssessment SampleAssessment(string reference) =>
        DeterministicCandidateIdentityResolver.Assess(
            new BookIdentity("The Lantern Road", "Mara Quill", [], Language: "en", Series: [new BookSeries("Series " + reference, null)]),
            new ExternalProviderCandidate(
                reference, new ExternalProviderWorkEvidence("The Lantern Road", null, [new BookAuthor("Mara Quill", "author")], [], []),
                new ExternalProviderEditionEvidence("en", 2019, null, []),
                new ExternalProviderReleaseEvidence("The Lantern Road sample", "epub", 10, false, null, true, null, null, [], null)));

    private static async Task EnableGatewayAsync(FamilyLibrarianAppFactory factory, bool enabled = true)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        using var saved = await http.PutAsJsonAsync("/api/v1/admin/model-gateway/", new SaveModelGatewaySettingsRequest(
            "http://litellm.example.test:4000", false,
            [new ModelRoleDto("CandidateAmbiguityAssessment", true, "Local", "local/alias", null)]));
        saved.EnsureSuccessStatusCode();
        using var switched = await http.PutAsJsonAsync("/api/v1/admin/model-gateway/enabled", new SetModelGatewayEnabledRequest(enabled));
        switched.EnsureSuccessStatusCode();
    }

    private const string Answer =
        "{\"schema\":\"candidate-assessment/1\",\"verdict\":\"uncertain\",\"claims\":[{\"kind\":\"needs_review\",\"evidence_ids\":[\"ev-condition-1\"],\"note\":\"It looks like a sample.\"}]}";

    [TestMethod]
    public async Task WithTheGatewayOffTheAnalyzerMakesNoCallAndStoresNothing()
    {
        var client = new ScriptedClient(() => ScriptedModel(Answer));
        await using var factory = Factory(client);
        await EnableGatewayAsync(factory, enabled: false);
        using var scope = factory.Services.CreateScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<CandidateSemanticAnalyzer>();
        var assessment = SampleAssessment("ref-off-" + Guid.NewGuid().ToString("N"));

        var report = await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Refused, report.Result);
        Assert.AreEqual(0, client.Calls);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(0, await database.SemanticFindings.CountAsync(finding => finding.SubjectReference == assessment.SourceMetadata.ProviderReference));
    }

    [TestMethod]
    public async Task AnEnabledRoleStoresAFindingAndALedgerRowAndNeverTouchesTheAssessment()
    {
        var client = new ScriptedClient(() => ScriptedModel(Answer));
        await using var factory = Factory(client);
        await EnableGatewayAsync(factory);
        using var scope = factory.Services.CreateScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<CandidateSemanticAnalyzer>();
        var assessment = SampleAssessment("ref-on-" + Guid.NewGuid().ToString("N"));
        var decisionBefore = assessment.Decision;

        var report = await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);
        var second = await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);

        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result);
        Assert.AreEqual(SemanticAnalysisResult.Reused, second.Result);
        Assert.AreEqual(1, client.Calls);
        Assert.AreEqual(decisionBefore, assessment.Decision);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finding = await database.SemanticFindings.AsNoTracking().SingleAsync(row => row.SubjectReference == assessment.SourceMetadata.ProviderReference);
        Assert.AreEqual(ModelCallOutcome.Succeeded, finding.Outcome);
        Assert.AreEqual("Uncertain", finding.Verdict);
        Assert.AreEqual(decisionBefore.ToString(), finding.DeterministicDecision);
        Assert.AreEqual("local/alias", finding.Model);
        Assert.IsTrue(await database.ModelUsageRecords.AnyAsync(row => row.Role == ModelRole.CandidateAmbiguityAssessment && row.BudgetClass == ModelBudgetClass.BackgroundValidation));

        var store = scope.ServiceProvider.GetRequiredService<ISemanticFindingStore>();
        var found = await store.FindLatestAsync(SemanticSubjectKind.Candidate, [CandidateSemanticEvidence.Digest(assessment)], CancellationToken.None);
        Assert.AreEqual(1, found.Count);
    }

    [TestMethod]
    public async Task AGarbageAnswerIsStoredAsAFailureWithNoVerdict()
    {
        var client = new ScriptedClient(() => ScriptedModel("Sure, I think it matches!"));
        await using var factory = Factory(client);
        await EnableGatewayAsync(factory);
        using var scope = factory.Services.CreateScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<CandidateSemanticAnalyzer>();
        var assessment = SampleAssessment("ref-bad-" + Guid.NewGuid().ToString("N"));

        var report = await analyzer.AnalyzeAsync(null, assessment, CancellationToken.None);
        Assert.AreEqual(SemanticAnalysisResult.Recorded, report.Result, $"{report.Outcome} {report.Error}");

        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finding = await database.SemanticFindings.AsNoTracking().SingleAsync(row => row.SubjectReference == assessment.SourceMetadata.ProviderReference);
        Assert.AreEqual(ModelCallOutcome.InvalidJson, finding.Outcome);
        Assert.IsNull(finding.Verdict);
        Assert.IsNull(finding.ClaimsJson);
        Assert.IsFalse((finding.FailureDetail ?? string.Empty).Contains("Sure, I think", StringComparison.Ordinal), "The model's text is not kept.");
    }

    private static ModelResponse ScriptedModel(string content) =>
        new(true, ModelFailureKind.None, null, content, [], "stop", "reported", new ModelUsage(100, 20, 120), 5);
}
