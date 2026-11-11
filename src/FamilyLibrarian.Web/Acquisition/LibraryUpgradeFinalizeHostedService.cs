using FamilyLibrarian.Application.Upgrades;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>Confirms delivered upgrade copies in their destination and removes the incomplete copy they replace, every few minutes.</summary>
public sealed partial class LibraryUpgradeFinalizeHostedService(IServiceScopeFactory scopeFactory, ILogger<LibraryUpgradeFinalizeHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var finished = await scope.ServiceProvider.GetRequiredService<LibraryUpgradeService>().FinalizeAsync(stoppingToken);
                    if (finished > 0)
                    {
                        LogFinished(finished);
                    }
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(exception);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Finished {Count} library upgrades.")]
    private partial void LogFinished(int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Library upgrade finishing pass failed.")]
    private partial void LogFailed(Exception exception);
}
