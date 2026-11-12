using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

public interface IModelWorkflowObserver
{
    Task ObserveAsync(Guid? usageRecordId, ModelWorkflowEffect effects, CancellationToken cancellationToken);
}

/// <summary>Best-effort telemetry. It never grants permission, changes a decision or fails a completed workflow.</summary>
public sealed class ModelWorkflowObserver(IModelUsageLedger ledger) : IModelWorkflowObserver
{
    public async Task ObserveAsync(Guid? usageRecordId, ModelWorkflowEffect effects, CancellationToken cancellationToken)
    {
        if (usageRecordId is not { } id) return;
        try { await ledger.ObserveWorkflowAsync(id, effects, cancellationToken); }
        catch (Exception) { /* Missing telemetry remains unknown; it cannot change the workflow outcome. */ }
    }
}
