using System.Net.Http.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

internal sealed class WorkflowProbe : IModelWorkflowObserver
{
    internal System.Collections.Concurrent.ConcurrentBag<(Guid Id, ModelWorkflowEffect Effects)> Events { get; } = [];
    public Task ObserveAsync(Guid? usageRecordId, ModelWorkflowEffect effects, CancellationToken cancellationToken)
    {
        if (usageRecordId is { } id) Events.Add((id, effects));
        return Task.CompletedTask;
    }
}

[TestClass]
public sealed class AiQualityEndpointTests
{
    private static readonly string[] ExpectedSearchEffects = ["ExtraSearchCompleted", "ExtraSearchReturnedResults"];
    [TestMethod]
    public async Task AtomicEffectsPersistWithoutLostUpdatesAndSummarySeparatesModelsRecommendationsAndUnknownHistory()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await fixture.CreateAdminClientAsync();
        using var scope = fixture.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var call = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null, "model-a", false, 100, now);
        var legacy = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null, "model-b", false, 100, now);
        var failed = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null, "model-a", false, 100, now);
        foreach (var record in new[] { call, legacy, failed }) await ledger.AddAsync(record, CancellationToken.None);
        await ledger.CompleteAsync(call.Id, ModelCallOutcome.Succeeded, 123, null, CancellationToken.None);
        await ledger.CompleteAsync(legacy.Id, ModelCallOutcome.Succeeded, 123, null, CancellationToken.None);
        await ledger.CompleteAsync(failed.Id, ModelCallOutcome.SchemaRejected, 123, null, CancellationToken.None, ModelFailureReason.RepeatedSearch);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => ledger.ObserveWorkflowAsync(call.Id,
            i % 2 == 0 ? ModelWorkflowEffect.ExtraSearchCompleted : ModelWorkflowEffect.ExtraSearchReturnedResults, CancellationToken.None)));
        await ledger.ObserveWorkflowAsync(failed.Id, ModelWorkflowEffect.FileHoldApplied, CancellationToken.None);
        database.SemanticFindings.Add(new(SemanticSubjectKind.SearchGuidance, "sample", null, null, "search-guidance/1", "digest", "NoAllowedCandidate",
            ModelRole.SearchGuidance, "model-a", false, ModelCallOutcome.Succeeded, "Search", "[]", null, now, call.Id));
        database.SemanticFindings.Add(new(SemanticSubjectKind.SearchGuidance, "sample2", null, null, "search-guidance/1", "digest2", "NoAllowedCandidate",
            ModelRole.SearchGuidance, "model-b", false, ModelCallOutcome.Succeeded, "private-verdict-must-not-leak", "[]", null, now));
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        Assert.AreEqual(call.Id, (await database.SemanticFindings.SingleAsync(f => f.SubjectReference == "sample")).ModelUsageRecordId);
        var summary = (await admin.GetFromJsonAsync<ModelUsageResponse>("/api/v1/admin/model-gateway/usage"))!;
        var used = summary.Rows.Single(row => row.Model == "model-a" && row.Outcome == "Succeeded");
        CollectionAssert.AreEquivalent(ExpectedSearchEffects, used.WorkflowEffects!.ToArray());
        Assert.IsNull(summary.Rows.Single(row => row.Model == "model-b").WorkflowEffects);
        Assert.IsNull(summary.Rows.Single(row => row.Outcome == "SchemaRejected").WorkflowEffects);
        Assert.IsTrue(summary.Recommendations!.Any(row => row.Model == "model-a" && row.Verdict == "Search" && row.Findings == 1));
        Assert.IsTrue(summary.Recommendations!.Any(row => row.Model == "model-b" && row.Verdict == "Other"));
        Assert.IsFalse((await admin.GetStringAsync("/api/v1/admin/model-gateway/usage")).Contains("private-verdict", StringComparison.Ordinal));
    }
}
