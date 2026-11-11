using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// C1-d over the real host and PostgreSQL: windows and latency, opportunity flags kept apart from observed actions, weekly rollups that
/// survive the prune, and what happened after the AI acted.
/// </summary>
[TestClass]
public sealed class AiInsightTests
{
    private const string BasePath = "/api/v1/admin/model-gateway";
    private static readonly string[] VetoOnly = ["TriageVetoEligible"];
    private static readonly string[] NoChangeOnly = ["NoChange"];
    private static readonly string[] VetoAndTied = ["TriageVetoEligible", "TriageTiedPreferEligible"];
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

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    private static Task<HttpClient> AdminAsync(FamilyLibrarianAppFactory factory) =>
        SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

    private static ModelUsageRecord Record(
        ModelRole role, string model, DateTimeOffset at, ModelCallOutcome outcome, long? latency, int? tokens = null,
        ModelWorkflowEffect effects = ModelWorkflowEffect.None, ModelFailureReason? reason = null, bool isCloud = false)
    {
        var record = new ModelUsageRecord(role, ModelBudgetClass.BackgroundValidation, null, model, isCloud, 100, at);
        if (outcome != ModelCallOutcome.Pending) record.Complete(outcome, latency ?? 0, null, null, tokens, reason);
        if (effects != ModelWorkflowEffect.None && outcome == ModelCallOutcome.Succeeded) record.ObserveWorkflow(effects);
        return record;
    }

    private static async Task ClearAsync(AppDbContext database)
    {
        await database.DecisionSnapshots.ExecuteDeleteAsync();
        await database.ModelUsageRecords.ExecuteDeleteAsync();
        await database.ModelUsageRollups.ExecuteDeleteAsync();
    }

    private static DateTimeOffset MondayOf(DateTimeOffset moment)
    {
        var date = moment.UtcDateTime.Date;
        return new DateTimeOffset(date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), TimeSpan.Zero);
    }

    [TestMethod]
    public void TheDomainAndWebCategoriesAgreeForEveryOutcomeAndReason()
    {
        foreach (var outcome in Enum.GetValues<ModelCallOutcome>())
            foreach (var reason in new ModelFailureReason?[] { null, ModelFailureReason.Cancelled, ModelFailureReason.InvalidStructure })
            {
                var web = ModelCallCategories.Classify(outcome.ToString(), reason?.ToString());
                var domain = ModelCallClassification.Classify(outcome, reason) switch
                {
                    ModelCallCategory.AcceptedReply => ModelCallCategories.AcceptedReply,
                    ModelCallCategory.RejectedReply => ModelCallCategories.RejectedReply,
                    ModelCallCategory.ProviderError => ModelCallCategories.ProviderError,
                    ModelCallCategory.Cancelled => ModelCallCategories.Cancelled,
                    ModelCallCategory.Pending => ModelCallCategories.Pending,
                    _ => ModelCallCategories.Unknown
                };
                Assert.AreEqual(web, domain, $"{outcome}/{reason}");
            }
    }

    [TestMethod]
    public async Task TheWindowSelectsTheRowsAndLatencyIsRealMedianAndNinetyFifthPercentile()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ClearAsync(database);
            var now = DateTimeOffset.UtcNow;
            // Ten finished calls of 100..1000 ms: median (nearest rank 5) 500, p95 (rank 10) 1000. One long-ago call, one pending, one refused-style zero.
            for (var i = 1; i <= 10; i++)
                database.ModelUsageRecords.Add(Record(ModelRole.CandidateTriage, "m1", now.AddHours(-2), ModelCallOutcome.Succeeded, i * 100, 10));
            database.ModelUsageRecords.Add(Record(ModelRole.CandidateTriage, "m1", now.AddDays(-5), ModelCallOutcome.Succeeded, 9_000, 10));
            database.ModelUsageRecords.Add(Record(ModelRole.CandidateTriage, "m1", now.AddHours(-1), ModelCallOutcome.Pending, null));
            await database.SaveChangesAsync();
        }

        var day = (await admin.GetFromJsonAsync<ModelUsageResponse>($"{BasePath}/usage?days=1"))!;
        var week = (await admin.GetFromJsonAsync<ModelUsageResponse>($"{BasePath}/usage?days=7"))!;

        Assert.AreEqual(1, day.Days);
        Assert.AreEqual(7, week.Days);
        Assert.AreEqual(10, day.Rows.Where(r => r.Outcome != "Pending").Sum(r => r.Calls), "Day: the ten recent finished calls; the older one is outside the window.");
        Assert.AreEqual(1, day.Rows.Where(r => r.Outcome == "Pending").Sum(r => r.Calls));
        Assert.AreEqual(10, day.Rows.Where(r => r.Outcome == "Succeeded").Sum(r => r.Calls));
        Assert.AreEqual(11, week.Rows.Where(r => r.Outcome == "Succeeded").Sum(r => r.Calls));
        var latency = day.Latency!.Single();
        Assert.AreEqual(10, latency.Calls, "Pending is not a finished call.");
        Assert.AreEqual(500, latency.MedianMilliseconds);
        Assert.AreEqual(1_000, latency.P95Milliseconds);
        Assert.AreEqual(100, latency.ReportedTokens);
        Assert.AreEqual(9_000, week.Latency!.Single().P95Milliseconds, "The longer window includes the slow call.");
    }

    [TestMethod]
    public async Task OnlyOneSevenThirtyOrNinetyDaysIsAccepted()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var bad in new[] { 0, 2, 31, 91, -1, 365 })
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync($"{BasePath}/usage?days={bad}")).StatusCode, $"days={bad}");
            Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync($"{BasePath}/usage/effectiveness?days={bad}")).StatusCode, $"effectiveness days={bad}");
        }

        foreach (var path in new[] { "/usage", "/usage/trend", "/usage/effectiveness" })
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(BasePath + path)).StatusCode, path);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync(BasePath + path)).StatusCode, path);
        }

        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync($"{BasePath}/usage/trend?weeks=0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync($"{BasePath}/usage/trend?weeks=157")).StatusCode);
    }

    [TestMethod]
    public async Task OpportunityFlagsAreReportedApartAndDoNotCountAsAnObservedEffect()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ClearAsync(database);
            var now = DateTimeOffset.UtcNow.AddHours(-1);
            database.ModelUsageRecords.AddRange(
                Record(ModelRole.CandidateTriage, "m", now, ModelCallOutcome.Succeeded, 10, null, ModelWorkflowEffect.TriageVetoEligible),
                Record(ModelRole.CandidateTriage, "m", now, ModelCallOutcome.Succeeded, 10, null,
                    ModelWorkflowEffect.TriageVetoEligible | ModelWorkflowEffect.TriageTiedPreferEligible | ModelWorkflowEffect.NoChange),
                Record(ModelRole.CandidateTriage, "m", now, ModelCallOutcome.Succeeded, 10));
            await database.SaveChangesAsync();
        }

        var usage = (await admin.GetFromJsonAsync<ModelUsageResponse>($"{BasePath}/usage?days=1"))!;

        var onlyOpportunity = usage.Rows.Single(r => r.Opportunities is { Count: 1 } && r.WorkflowEffects is null);
        CollectionAssert.AreEqual(VetoOnly, onlyOpportunity.Opportunities!.ToArray());
        var both = usage.Rows.Single(r => r.WorkflowEffects is not null);
        CollectionAssert.AreEquivalent(NoChangeOnly, both.WorkflowEffects!.ToArray());
        CollectionAssert.AreEquivalent(VetoAndTied, both.Opportunities!.ToArray());
        Assert.IsTrue(usage.Rows.Any(r => r.Opportunities is null && r.WorkflowEffects is null), "A call with nothing recorded stays untracked.");
    }

    [TestMethod]
    public async Task TheRealLedgerStoresAnOpportunityFlagOnASucceededCallAndRefusesAnUnknownBit()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ClearAsync(database);
        var ledger = scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var ok = Record(ModelRole.CandidateTriage, "m", DateTimeOffset.UtcNow, ModelCallOutcome.Succeeded, 10);
        var failed = Record(ModelRole.CandidateTriage, "m", DateTimeOffset.UtcNow, ModelCallOutcome.SchemaRejected, 10);
        database.ModelUsageRecords.AddRange(ok, failed);
        await database.SaveChangesAsync();

        await ledger.ObserveWorkflowAsync(ok.Id, ModelWorkflowEffect.TriageTiedPreferEligible, CancellationToken.None);
        await ledger.ObserveWorkflowAsync(ok.Id, ModelWorkflowEffect.NoChange, CancellationToken.None);
        await ledger.ObserveWorkflowAsync(failed.Id, ModelWorkflowEffect.TriageTiedPreferEligible, CancellationToken.None);

        var stored = await database.ModelUsageRecords.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.WorkflowEffects);
        Assert.AreEqual((int)(ModelWorkflowEffect.TriageTiedPreferEligible | ModelWorkflowEffect.NoChange), stored[ok.Id], "Flags and effects share the column and add up.");
        Assert.IsNull(stored[failed.Id], "Only a succeeded call carries observations.");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            ledger.ObserveWorkflowAsync(ok.Id, (ModelWorkflowEffect)(1 << 22), CancellationToken.None));
    }

    [TestMethod]
    public async Task OldCallsAreFoldedIntoWeeklyCountsExactlyOnceAndSurviveThePrune()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ClearAsync(database);
        var ledger = (ModelUsageLedger)scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var old = DateTimeOffset.UtcNow.AddDays(-120);
        var week = MondayOf(old);
        var searchFlag = ModelWorkflowEffect.SearchAfterFixedSearches;
        var r1 = Record(ModelRole.SearchGuidance, "m1", old, ModelCallOutcome.Succeeded, 100, 10, searchFlag | ModelWorkflowEffect.ExtraSearchCompleted);
        var r2 = Record(ModelRole.SearchGuidance, "m1", old, ModelCallOutcome.Succeeded, 200, 20, searchFlag | ModelWorkflowEffect.NoChange);
        var r3 = Record(ModelRole.SearchGuidance, "m1", old, ModelCallOutcome.SchemaRejected, 300, null);
        var r4 = Record(ModelRole.SearchGuidance, "m1", old, ModelCallOutcome.Timeout, 50, null);
        var pending = Record(ModelRole.SearchGuidance, "m1", old, ModelCallOutcome.Pending, null);
        var recent = Record(ModelRole.SearchGuidance, "m1", DateTimeOffset.UtcNow.AddDays(-10), ModelCallOutcome.Succeeded, 70, 5);
        database.ModelUsageRecords.AddRange(r1, r2, r3, r4, pending, recent);
        // r1's decision later had a good fact; r2's had both a good and a bad one (mixed counts as neither).
        var s1 = new DecisionSnapshot(ModelRole.SearchGuidance, 1, "a", "d1", "{}", null, null, null, false, old);
        var s2 = new DecisionSnapshot(ModelRole.SearchGuidance, 1, "b", "d2", "{}", null, null, null, false, old);
        database.DecisionSnapshots.AddRange(s1, s2);
        database.DecisionCalls.AddRange(
            new DecisionCall(s1.Id, r1.Id, null, null, ModelPlacement.Local, "m1", null, ModelReasoningMode.Inherit, "h", "p", ModelCallOutcome.Succeeded, null, null, old),
            new DecisionCall(s2.Id, r2.Id, null, null, ModelPlacement.Local, "m1", null, ModelReasoningMode.Inherit, "h", "p", ModelCallOutcome.Succeeded, null, null, old));
        database.DecisionOutcomes.AddRange(
            new DecisionOutcome(s1.Id, null, DecisionOutcomeKind.SearchFoundSelectedResult, "t", false, DecisionOutcomeStrength.Strong, old),
            new DecisionOutcome(s2.Id, null, DecisionOutcomeKind.SearchFoundSelectedResult, "t", false, DecisionOutcomeStrength.Strong, old),
            new DecisionOutcome(s2.Id, null, DecisionOutcomeKind.SearchReturnedNoResults, "t", false, DecisionOutcomeStrength.Weak, old));
        await database.SaveChangesAsync();

        await ledger.RunPruneNowAsync(CancellationToken.None);
        await ledger.RunPruneNowAsync(CancellationToken.None);

        var rows = await database.ModelUsageRollups.AsNoTracking().OrderBy(r => r.OpportunityFlags).ToListAsync();
        Assert.HasCount(2, rows, "One row per opportunity combination for that week.");
        Assert.IsTrue(rows.All(r => r.WeekStartUtc == week && r.Role == ModelRole.SearchGuidance && r.Model == "m1"));
        var plain = rows[0];
        Assert.AreEqual(0, plain.OpportunityFlags);
        Assert.AreEqual(2, plain.Calls, "r3 and r4; the pending call is never counted.");
        Assert.AreEqual(1, plain.RejectedReplies);
        Assert.AreEqual(1, plain.ProviderErrors);
        Assert.AreEqual(2, plain.CallsWithoutUsage);
        Assert.AreEqual(350, plain.LatencyMillisecondsSum);
        Assert.AreEqual(300, plain.LatencyMillisecondsMax);
        Assert.AreEqual(300, plain.LatencyP95Milliseconds);
        var flagged = rows[1];
        Assert.AreEqual((int)searchFlag, flagged.OpportunityFlags);
        Assert.AreEqual(2, flagged.Calls);
        Assert.AreEqual(2, flagged.AcceptedReplies);
        Assert.AreEqual(1, flagged.ActionCalls);
        Assert.AreEqual(1, flagged.NoChangeCalls);
        Assert.AreEqual(30, flagged.ReportedTokens);
        Assert.AreEqual(300, flagged.LatencyMillisecondsSum);
        Assert.AreEqual(200, flagged.LatencyP95Milliseconds);
        Assert.AreEqual(1, flagged.DownstreamPositive, "r1's decision had only a good fact.");
        Assert.AreEqual(0, flagged.DownstreamNegative, "r2's decision was mixed, so it counts as neither.");
        var remaining = await database.ModelUsageRecords.AsNoTracking().Select(r => r.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { recent.Id }, remaining, "Old calls (including the pending one) are gone; the recent call stays.");
    }

    [TestMethod]
    public async Task ASecondPruneOfTheSameWeekAddsUpAndKeepsTheLargerNinetyFifthPercentile()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ClearAsync(database);
        var ledger = (ModelUsageLedger)scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var old = DateTimeOffset.UtcNow.AddDays(-120);
        database.ModelUsageRecords.Add(Record(ModelRole.CandidateTriage, "m", old, ModelCallOutcome.Succeeded, 200, 10));
        await database.SaveChangesAsync();
        await ledger.RunPruneNowAsync(CancellationToken.None);
        database.ModelUsageRecords.Add(Record(ModelRole.CandidateTriage, "m", old, ModelCallOutcome.Succeeded, 900, 5));
        await database.SaveChangesAsync();

        await ledger.RunPruneNowAsync(CancellationToken.None);

        var row = await database.ModelUsageRollups.AsNoTracking().SingleAsync();
        Assert.AreEqual(2, row.Calls);
        Assert.AreEqual(15, row.ReportedTokens);
        Assert.AreEqual(1_100, row.LatencyMillisecondsSum);
        Assert.AreEqual(900, row.LatencyP95Milliseconds, "An upper bound across the two clean-ups, never an understatement.");
    }

    [TestMethod]
    public async Task TheTrendEndpointReturnsKeptWeeksAndPurgingEverythingClearsThem()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ClearAsync(database);
            database.ModelUsageRecords.Add(Record(ModelRole.CandidateTriage, "trend-model", DateTimeOffset.UtcNow.AddDays(-100), ModelCallOutcome.Succeeded, 120, 7,
                ModelWorkflowEffect.CandidateVetoUsed | ModelWorkflowEffect.TriageVetoEligible));
            await database.SaveChangesAsync();
            await ((ModelUsageLedger)scope.ServiceProvider.GetRequiredService<IModelUsageLedger>()).RunPruneNowAsync(CancellationToken.None);
        }

        var trend = (await admin.GetFromJsonAsync<ModelUsageTrendResponse>($"{BasePath}/usage/trend?weeks=52"))!;

        var row = trend.Rows.Single();
        Assert.AreEqual("CandidateTriage", row.Role);
        Assert.AreEqual("trend-model", row.Model);
        Assert.AreEqual(1, row.ActionCalls);
        CollectionAssert.AreEqual(VetoOnly, row.Opportunities.ToArray());
        var insight = (await admin.GetFromJsonAsync<DecisionCaptureInsightResponse>($"{BasePath}/capture/insight"))!;
        Assert.AreEqual(1, insight.Tables.Single(t => t.Table == "Weekly usage counts").Rows, "The size card lists the weekly counts.");

        using var purge = await admin.PostAsJsonAsync($"{BasePath}/capture/purge", new PurgeDecisionCaptureRequest("CandidateTriage"));
        Assert.AreEqual(1, (await admin.GetFromJsonAsync<ModelUsageTrendResponse>($"{BasePath}/usage/trend?weeks=52"))!.Rows.Count, "Purging one role leaves the weekly counts.");
        using var all = await admin.PostAsJsonAsync($"{BasePath}/capture/purge", new PurgeDecisionCaptureRequest(null));
        Assert.AreEqual(1, (await all.Content.ReadFromJsonAsync<DecisionCapturePurgeResponse>())!.UsageRollups);
        Assert.AreEqual(0, (await admin.GetFromJsonAsync<ModelUsageTrendResponse>($"{BasePath}/usage/trend?weeks=52"))!.Rows.Count);
        Assert.IsTrue(purge.IsSuccessStatusCode);
    }

    [TestMethod]
    public async Task WhatHappenedNextCountsActedCallsAndTheirLaterFactsWithoutScoringAnything()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ClearAsync(database);
            var now = DateTimeOffset.UtcNow.AddDays(-2);
            var acted = new List<(ModelUsageRecord Record, DecisionSnapshot Snapshot)>();
            for (var i = 0; i < 5; i++)
            {
                var record = Record(ModelRole.CandidateTriage, "m", now, ModelCallOutcome.Succeeded, 10, null, ModelWorkflowEffect.CandidateVetoUsed);
                var snapshot = new DecisionSnapshot(ModelRole.CandidateTriage, 1, "s" + i, "d" + i, "{}", null, null, null, false, now);
                acted.Add((record, snapshot));
                database.ModelUsageRecords.Add(record);
                database.DecisionSnapshots.Add(snapshot);
                database.DecisionCalls.Add(new DecisionCall(snapshot.Id, record.Id, null, null, ModelPlacement.Local, "m", null, ModelReasoningMode.Inherit, "h", "p",
                    ModelCallOutcome.Succeeded, null, null, now));
            }

            // One that only agreed (no action): never counted.
            var agreed = Record(ModelRole.CandidateTriage, "m", now, ModelCallOutcome.Succeeded, 10, null, ModelWorkflowEffect.NoChange);
            var agreedSnapshot = new DecisionSnapshot(ModelRole.CandidateTriage, 1, "agree", "dx", "{}", null, null, null, false, now);
            database.ModelUsageRecords.Add(agreed);
            database.DecisionSnapshots.Add(agreedSnapshot);
            database.DecisionCalls.Add(new DecisionCall(agreedSnapshot.Id, agreed.Id, null, null, ModelPlacement.Local, "m", null, ModelReasoningMode.Inherit, "h", "p",
                ModelCallOutcome.Succeeded, null, null, now));
            DecisionOutcome Fact(DecisionSnapshot s, DecisionOutcomeKind kind, bool influenced = false) =>
                new(s.Id, null, kind, "t", influenced, DecisionOutcomeStrength.Strong, now);
            database.DecisionOutcomes.AddRange(
                Fact(acted[0].Snapshot, DecisionOutcomeKind.CandidateDownloadedVerified, influenced: true),
                Fact(acted[1].Snapshot, DecisionOutcomeKind.VerificationFailed),
                Fact(acted[2].Snapshot, DecisionOutcomeKind.CandidateDownloadedVerified), Fact(acted[2].Snapshot, DecisionOutcomeKind.RequesterDeclined),
                Fact(agreedSnapshot, DecisionOutcomeKind.VerificationFailed));
            await database.SaveChangesAsync();
        }

        var result = (await admin.GetFromJsonAsync<AiEffectivenessResponse>($"{BasePath}/usage/effectiveness?days=30"))!;

        var row = result.Roles.Single();
        Assert.AreEqual("CandidateTriage", row.Role);
        Assert.AreEqual(5, row.ActedCalls);
        Assert.AreEqual(3, row.WithFacts, "Two acted decisions never got a result; silence is not counted as one.");
        Assert.AreEqual(1, row.PositiveOnly);
        Assert.AreEqual(1, row.NegativeOnly);
        Assert.AreEqual(1, row.Mixed);
        Assert.AreEqual(1, row.InfluencedByNote);
    }
}
