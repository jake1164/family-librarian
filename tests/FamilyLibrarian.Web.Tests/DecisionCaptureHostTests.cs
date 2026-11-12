using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// C1-b through the real host: the real dependency graph, policy, ledger, findings and PostgreSQL. The model is scripted, everything else is
/// production wiring, so this proves each service really receives the sink and that what is saved reproduces the prompt that was sent.
/// </summary>
[TestClass]
public sealed class DecisionCaptureHostTests
{
    private const string Capture = "/api/v1/admin/model-gateway/capture/";
    private static readonly BookIdentity Requested = new("The Lantern Road", "Mara Quill", [], [new("Mara Quill", "author")], [new("Lantern Cycle", "2")], "en", 2019, "Ember Press");

    private sealed class RoleModel : IModelClient
    {
        public List<string> Systems { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken)
        {
            var system = request.Messages[0].Content;
            Systems.Add(system);
            var reply = system.Contains("Propose bounded bibliographic searches", StringComparison.Ordinal)
                ? "{\"schema\":\"search-guidance/1\",\"give_up\":false,\"suggestions\":[{\"reason\":\"alternate_published_title\",\"value\":\"A New Journey\",\"evidence_ids\":[\"req-title\"],\"note\":\"Try a new title.\"}]}"
                : system.Contains("Compare supplied candidates", StringComparison.Ordinal)
                    ? "{\"schema\":\"candidate-triage/1\",\"move\":\"keep\",\"candidate\":null,\"evidence_ids\":[],\"note\":\"No move is supported.\"}"
                    : system.Contains("You read one message", StringComparison.Ordinal)
                        ? "{\"schema\":\"book-intent/1\",\"intent\":\"find_book\",\"title\":\"The Lantern Road\",\"author\":null}"
                        : "{\"schema\":\"candidate-assessment/1\",\"verdict\":\"uncertain\",\"claims\":[{\"kind\":\"insufficient_information\",\"evidence_ids\":[],\"note\":\"Not enough evidence.\"}]}";
            return Task.FromResult(new ModelResponse(true, ModelFailureKind.None, null, reply, [], "stop", "host/reported-model", new(10, 10, 20), 1));
        }
    }

    private static FulfillmentOption Option(int number, string title, string release, bool sample = false)
    {
        var source = new ExternalProviderCandidate("ref-" + number, new(title, null, [new("Mara Quill", "author")], [], []),
            new("en", 2019, "Ember Press", [new("isbn13", "9781234567897")]),
            new(release, "epub", 1_500_000 + number, false, null, sample, false, null, [], null), "revision-SECRET", "token-SECRET");
        var assessment = DeterministicCandidateIdentityResolver.Assess(Requested, source);
        return new("example-source", "result-" + number, Guid.Empty, null, RequestMediaType.Ebook, OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload,
            "epub", "en", null, null, null, null, null, null, null, "provider-data-SECRET", Title: title, Author: "Mara Quill", CandidateRevision: "revision-SECRET",
            AcquireToken: "token-SECRET", PublicationYear: 2019, Publisher: "Ember Press", SizeBytes: 1_500_000 + number, ReleaseName: release,
            AuthorAffinity: assessment.AuthorEvidence, HasPlausibleTitle: true, IdentityAssessment: assessment);
    }

    [TestMethod]
    public async Task EveryServiceInTheRealHostSavesItsDecisionAndTheSavedInputReproducesThePromptThatWasSent()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var model = new RoleModel();
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IModelClient>();
            services.AddSingleton<IModelClient>(model);
        });
        using var admin = factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword })).StatusCode);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var save = await admin.PutAsJsonAsync("/api/v1/admin/model-gateway/", new SaveModelGatewaySettingsRequest("http://example.test", false,
        [
            new("SearchGuidance", true, "Local", "test/search", null), new("CandidateTriage", true, "Local", "test/triage", null),
            new("IntentInterpretation", true, "Local", "test/intent", null), new("CandidateAmbiguityAssessment", true, "Local", "test/ambig", null)
        ]));
        Assert.AreEqual(HttpStatusCode.OK, save.StatusCode, await save.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/model-gateway/enabled", new SetModelGatewayEnabledRequest(true))).StatusCode);
        var defaults = (await admin.GetFromJsonAsync<DecisionCaptureSettingsResponse>(Capture))!;
        Assert.IsTrue(defaults.DecisionCaptureEnabled, "Decisions are saved by default.");
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Capture, new SaveDecisionCaptureSettingsRequest(true, true, defaults.Limits))).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var work = new Work("The Lantern Road", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);
        var userId = await database.Users.Select(u => u.Id).FirstAsync();
        var request = new BookRequest(userId, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        await database.SaveChangesAsync();
        var format = request.Formats.Single().Id;
        var results = new[] { Option(1, "The Lantern Road", "The.Lantern.Road.Mara.Quill"), Option(2, "Lantern Road (Sample)", "Lantern.Road.Sample", sample: true) };

        var search = await scope.ServiceProvider.GetRequiredService<SearchGuidanceService>()
            .GetAsync(request.Id, format, Requested, results, ["Lantern Road"], now.AddMinutes(-1), CancellationToken.None);
        var triage = await scope.ServiceProvider.GetRequiredService<CandidateTriageService>().GetAsync(request.Id, format, "example-source",
            new CandidateTriageContext(Requested, results, results[0], [results[0].ProviderResultId, results[1].ProviderResultId], ProviderConfidenceLevel.Standard),
            CancellationToken.None);
        var ambiguity = await scope.ServiceProvider.GetRequiredService<CandidateSemanticAnalyzer>().AnalyzeAsync(request.Id, results[1].IdentityAssessment!, CancellationToken.None);
        var intent = await scope.ServiceProvider.GetRequiredService<IBookIntentInterpreter>().InterpretAsync(userId, "the lantern road please", "Lantern Cycle", CancellationToken.None);

        Assert.IsNotNull(search);
        Assert.IsNull(triage, "Triage said keep, so there is no move to apply; the decision was still saved.");
        Assert.AreEqual(SemanticAnalysisResult.Recorded, ambiguity.Result);
        Assert.AreEqual(BookIntent.FindBook, intent?.Intent);
        Assert.AreEqual(4, model.Systems.Count, "One call per role: capture added none.");

        database.ChangeTracker.Clear();
        var snapshots = await database.DecisionSnapshots.AsNoTracking().ToListAsync();
        CollectionAssert.AreEquivalent(
            new[] { ModelRole.SearchGuidance, ModelRole.CandidateTriage, ModelRole.CandidateAmbiguityAssessment, ModelRole.IntentInterpretation },
            snapshots.Select(s => s.Role).ToArray());
        foreach (var snapshot in snapshots)
        {
            Assert.IsNull(snapshot.StaleReason, $"{snapshot.Role}: the saved input must reproduce the prompt exactly through the real policy and database.");
            Assert.IsFalse(snapshot.SnapshotJson.Contains("SECRET", StringComparison.Ordinal), $"{snapshot.Role}: no token, revision or provider data.");
            Assert.IsFalse(snapshot.SnapshotJson.Contains("example-source", StringComparison.Ordinal), $"{snapshot.Role}: no provider id.");
            var call = await database.DecisionCalls.AsNoTracking().SingleAsync(c => c.SnapshotId == snapshot.Id);
            Assert.AreEqual("host/reported-model", call.ReportedModel);
            Assert.IsNotNull(call.ModelUsageRecordId);
            Assert.IsNotNull(call.SettingsId);
            Assert.AreEqual(ModelCallOutcome.Succeeded, call.Outcome);
        }

        Assert.AreEqual(userId, snapshots.Single(s => s.Role == ModelRole.IntentInterpretation).UserId);
        Assert.AreEqual(request.Id, snapshots.Single(s => s.Role == ModelRole.SearchGuidance).RequestId);
        var triageId = snapshots.Single(s => s.Role == ModelRole.CandidateTriage).Id;
        Assert.AreEqual(2, await database.CandidateLinks.CountAsync(link => link.SnapshotId == triageId));
    }
}
