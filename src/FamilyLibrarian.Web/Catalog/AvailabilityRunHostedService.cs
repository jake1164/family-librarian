using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Web.Catalog;

public sealed partial class AvailabilityRunHostedService(
    AvailabilityRunCoordinator coordinator,
    IServiceScopeFactory scopeFactory,
    ILogger<AvailabilityRunHostedService> logger) : BackgroundService
{
    private readonly SemaphoreSlim concurrency = new(4, 4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var run in coordinator.ReadPendingAsync(stoppingToken))
        {
            await concurrency.WaitAsync(stoppingToken);
            _ = ProcessAsync(run, stoppingToken);
        }
    }

    private async Task ProcessAsync(AvailabilityRun run, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, run.CancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var availability = scope.ServiceProvider.GetRequiredService<ICandidateAvailabilityService>();
            await foreach (var update in availability.GetAvailabilityUpdatesAsync(run.Identity, linked.Token))
            {
                run.Add(update.Options);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogRunFailed(exception, run.Id);
        }
        finally
        {
            run.Complete();
            run.Dispose();
            concurrency.Release();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Availability run {AvailabilityRunId} failed.")]
    private partial void LogRunFailed(Exception exception, Guid availabilityRunId);
}
