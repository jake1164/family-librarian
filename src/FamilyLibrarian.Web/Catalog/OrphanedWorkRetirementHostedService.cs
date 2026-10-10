using FamilyLibrarian.Infrastructure.Catalog;

namespace FamilyLibrarian.Web.Catalog;

/// <summary>Retires Works left behind only by cancelled requests, once at startup.</summary>
public sealed partial class OrphanedWorkRetirementHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<OrphanedWorkRetirementHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var retirement = scope.ServiceProvider.GetRequiredService<OrphanedWorkRetirement>();
            var retired = await retirement.RetireAsync(clock.GetUtcNow(), cancellationToken);
            if (retired > 0)
            {
                LogRetired(retired);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Housekeeping only: never stop the host from starting over it.
            LogFailed(exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Retired {WorkCount} catalog works that only had cancelled requests.")]
    private partial void LogRetired(int workCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Retiring works left only by cancelled requests failed; it will be retried on next start.")]
    private partial void LogFailed(Exception exception);
}
