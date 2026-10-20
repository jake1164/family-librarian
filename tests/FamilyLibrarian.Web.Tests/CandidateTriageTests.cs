using System.Text.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class CandidateTriageTests
{
    private static string Reply(string move, string? candidate = null, params string[] ids) => JsonSerializer.Serialize(new
        { schema = "candidate-triage/1", move, candidate, evidence_ids = ids, note = "Compare supplied bibliographic facts." });

    private static ExternalProviderCandidate Copy(string id, bool conflict = false) => VariantSearchProviderClient.Hobbit(id, true) with
    {
        Work = new("The Hobbit", null, [new(conflict ? "T. L. Morgan" : "J. R. R. Tolkien", "author")], [new("Middle-earth", "0")], []),
        Release = VariantSearchProviderClient.Hobbit(id, true).Release! with { Name = $"The Hobbit {id}.epub" }
    };

    [TestMethod]
    [DataRow("Strict")]
    [DataRow("Standard")]
    [DataRow("Liberal")]
    public async Task GroundedVetoSkipsOnlyTheCurrentPickAndFreshSubmissionUsesTheNextCandidate(string level)
    {
        var usageId = Guid.NewGuid();
        var workflow = new WorkflowProbe();
        var model = new Policy(Reply("veto", "c1", "c1.ev-condition-drm")) { UsageRecordId = usageId };
        var a = Copy("a"); var b = Copy("b");
        a = a with { Release = a.Release! with { DrmStatus = ExternalProviderDrmStatus.Unknown } };
        b = b with { Release = b.Release! with { DrmStatus = ExternalProviderDrmStatus.Unknown } };
        var client = new VariantSearchProviderClient(_ => [a, b]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: level, configureServices: s =>
        {
            Configure(s, model);
            s.RemoveAll<IModelWorkflowObserver>(); s.AddSingleton<IModelWorkflowObserver>(workflow);
        });
        Assert.AreEqual(1, model.Calls);
        Assert.AreEqual("b", client.SubmittedCandidates.Single());
        Assert.AreEqual((usageId, ModelWorkflowEffect.CandidateVetoUsed), workflow.Events.Single());
    }

    [TestMethod]
    public async Task ChangedSourceFactsInvalidateAnAuthorVariantBeforeAnyProviderSubmission()
    {
        var model = new Policy(Reply("author_variant", "c1", "c1.ev-title", "c1.ev-series-1"));
        var candidate = Copy("variant", true);
        var client = new VariantSearchProviderClient(_ => [model.Calls == 0 ? candidate : candidate with
            { Edition = candidate.Edition! with { Language = "fr" } }]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: "Standard", configureServices: s => Configure(s, model));
        Assert.AreEqual(1, model.Calls);
        Assert.IsEmpty(client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task PreferChoosesOnlyASuppliedTiedCandidateAndReprovesItBeforeSubmission()
    {
        var model = new Policy(Reply("prefer", "c2", "c2.ev-title"));
        var client = new VariantSearchProviderClient(_ => [Copy("a"), Copy("b")]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: "Standard", configureServices: s => Configure(s, model));
        Assert.AreEqual(1, model.Calls);
        Assert.AreEqual("b", client.SubmittedCandidates.Single());
    }

    [TestMethod]
    [DataRow("Standard", 1)]
    [DataRow("Liberal", 1)]
    [DataRow("Strict", 0)]
    public async Task AuthorVariantNeedsIndependentSeriesAndHonoursTheEffectiveLevel(string level, int downloads)
    {
        var model = new Policy(Reply("author_variant", "c1", "c1.ev-title", "c1.ev-series-1"));
        var client = new VariantSearchProviderClient(_ => [Copy("variant", true)]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: level, configureServices: s => Configure(s, model),
            afterwards: async (factory, _, _) =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var jobs = await db.ProviderAcquisitionJobs.ToArrayAsync();
                Assert.IsTrue(jobs.All(job => !job.IdentityPreConfirmed), "AI candidate selection must not pre-confirm file identity.");
            });
        Assert.AreEqual(1, model.Calls);
        Assert.HasCount(downloads, client.SubmittedCandidates);
    }

    [TestMethod]
    [DataRow("keep")]
    [DataRow("uncertain")]
    [DataRow("invalid_json")]
    [DataRow("timeout")]
    [DataRow("transport")]
    [DataRow("refused")]
    [DataRow("unknown_candidate")]
    [DataRow("unknown_evidence")]
    [DataRow("positive_veto")]
    [DataRow("off")]
    public async Task EveryFailureAndDisabledRolePreservesTheDeterministicPick(string mode)
    {
        var text = mode switch
        {
            "unknown_candidate" => Reply("prefer", "c99", "c1.ev-title"),
            "unknown_evidence" => Reply("prefer", "c2", "c2.forged"),
            "positive_veto" => Reply("veto", "c1", "c1.ev-title"),
            "invalid_json" => "not JSON", _ => Reply(mode == "uncertain" ? "uncertain" : "keep")
        };
        var model = new Policy(text) { Failure = mode };
        var client = new VariantSearchProviderClient(_ => [Copy("a"), Copy("b")]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: "Standard", configureServices: s => Configure(s, model, mode != "off"));
        Assert.AreEqual(mode == "off" ? 0 : 1, model.Calls);
        Assert.AreEqual("a", client.SubmittedCandidates.Single());
    }

    private static void Configure(IServiceCollection services, Policy model, bool enabled = true)
    {
        services.RemoveAll<IModelRoleResolver>();
        services.AddSingleton<IModelRoleResolver>(new Resolver(enabled));
        services.RemoveAll<IModelExecutionPolicy>();
        services.AddSingleton<IModelExecutionPolicy>(model);
    }

    [TestMethod]
    public async Task AnAuthorVariantCannotDisplaceAnAvailableStrongCandidate()
    {
        var model = new Policy(Reply("author_variant", "c2", "c2.ev-title", "c2.ev-series-1"));
        var client = new VariantSearchProviderClient(_ => [Copy("a"), Copy("b", true)]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: "Standard", configureServices: s => Configure(s, model));
        Assert.AreEqual(1, model.Calls);
        Assert.AreEqual("a", client.SubmittedCandidates.Single());
    }

    [TestMethod]
    public async Task ANewStrongCandidateInFreshSourceFactsPreventsSubmittingAnAuthorVariant()
    {
        var model = new Policy(Reply("author_variant", "c1", "c1.ev-title", "c1.ev-series-1"));
        var variant = Copy("b", true);
        var client = new VariantSearchProviderClient(_ => model.Calls == 0 ? [variant] : [Copy("a"), variant]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: "Standard", configureServices: s => Configure(s, model));
        Assert.AreEqual(1, model.Calls);
        Assert.IsEmpty(client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task AVetoOfTheOnlyCopyRecordsTheActualReasonAndKeepsItsScheduledRecheck()
    {
        var model = new Policy(Reply("veto", "c1", "c1.ev-condition-drm"));
        var copy = Copy("a");
        copy = copy with { Release = copy.Release! with { DrmStatus = ExternalProviderDrmStatus.Unknown } };
        var client = new VariantSearchProviderClient(_ => [copy]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "triage-source", providerConfidence: "Standard", configureServices: s => Configure(s, model),
            afterwards: async (factory, _, _) =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var attempts = await db.ProviderAttempts.Where(attempt => attempt.ProviderId == "triage-source").ToArrayAsync();
                Assert.IsTrue(attempts.Any(attempt => attempt.Summary.Contains("Automated candidate review", StringComparison.Ordinal)));
                Assert.IsTrue(attempts.All(attempt => attempt.NextEligibleCheckAtUtc is not null));
                Assert.IsFalse(attempts.Any(attempt => attempt.Summary.Contains("other languages", StringComparison.Ordinal)));
            });
        Assert.AreEqual(1, model.Calls);
        Assert.IsEmpty(client.SubmittedCandidates);
    }

    private sealed class Resolver(bool enabled) : IModelRoleResolver
    {
        public Task<ModelRoleResolution> ResolveAsync(ModelRole role, CancellationToken cancellationToken) => Task.FromResult(
            role == ModelRole.CandidateTriage && enabled
                ? new ModelRoleResolution(role, ModelUnavailableReason.None, new Uri("http://model.test"), null, "triage-test", false, ModelExecutionLimits.Default)
                : ModelRoleResolution.Unavailable(role, ModelUnavailableReason.RoleDisabled));
    }

    private sealed class Policy(string text) : IModelExecutionPolicy
    {
        public Guid? UsageRecordId { get; init; }
        public int Calls { get; private set; }
        public string? Failure { get; init; }
        public Task<ModelExecution<T>> ExecuteAsync<T>(ModelExecutionRequest request, Func<string, ModelParseResult<T>> parse, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual(ModelRole.CandidateTriage, request.Role);
            Assert.AreEqual(300, request.MaxOutputTokens);
            Assert.AreEqual(ModelBudgetClass.BackgroundValidation, request.Budget);
            if (Failure == "timeout") throw new OperationCanceledException("Model timeout.");
            if (Failure == "transport") throw new HttpRequestException("Model failed.");
            if (Failure == "refused") return Task.FromResult(new ModelExecution<T>(ModelCallOutcome.RoleUnavailable, default, "Refused", null, 0, "test", false));
            var result = parse(text);
            return Task.FromResult(new ModelExecution<T>(result.Failure, result.Value, result.Detail, null, 1, "test", false, UsageRecordId: UsageRecordId));
        }
    }
}
