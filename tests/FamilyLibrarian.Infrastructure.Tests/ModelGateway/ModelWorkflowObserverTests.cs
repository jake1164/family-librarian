using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Infrastructure.Tests.ModelGateway;

internal sealed class RecordingWorkflowObserver : IModelWorkflowObserver
{
    public List<(Guid Id, ModelWorkflowEffect Effects)> Observations { get; } = [];
    public Task ObserveAsync(Guid? usageRecordId, ModelWorkflowEffect effects, CancellationToken cancellationToken)
    {
        if (usageRecordId is { } id) Observations.Add((id, effects));
        return Task.CompletedTask;
    }
}

[TestClass]
public sealed class ModelWorkflowObserverTests
{
    [TestMethod]
    public async Task ObservationsAreMonotonicIdempotentAndNeverMakeFailedCallsUseful()
    {
        var ledger = new FakeLedger();
        var call = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null, "example", false, 100, DateTimeOffset.UtcNow);
        ledger.Records.Add(call);
        var observer = new ModelWorkflowObserver(ledger);
        await observer.ObserveAsync(call.Id, ModelWorkflowEffect.ExtraSearchCompleted, CancellationToken.None);
        Assert.IsNull(call.WorkflowEffects, "A pending call must not claim applied effects.");
        call.Complete(ModelCallOutcome.Succeeded, 1, null, null, null);
        await observer.ObserveAsync(call.Id, ModelWorkflowEffect.ExtraSearchCompleted, CancellationToken.None);
        await observer.ObserveAsync(call.Id, ModelWorkflowEffect.ExtraSearchCompleted, CancellationToken.None);
        await observer.ObserveAsync(call.Id, ModelWorkflowEffect.ExtraSearchReturnedResults, CancellationToken.None);
        Assert.AreEqual((int)(ModelWorkflowEffect.ExtraSearchCompleted | ModelWorkflowEffect.ExtraSearchReturnedResults), call.WorkflowEffects);
        await observer.ObserveAsync(Guid.NewGuid(), ModelWorkflowEffect.FileHoldApplied, CancellationToken.None);
        await observer.ObserveAsync(null, ModelWorkflowEffect.FileHoldApplied, CancellationToken.None);
    }
}
