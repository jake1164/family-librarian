using FamilyLibrarian.Application.Upgrades;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>Looks for a better copy of delivered books that look incomplete, once a day (each book at most every 30 days).</summary>
public sealed partial class LibraryUpgradeHostedService(IServiceScopeFactory scopeFactory, ILogger<LibraryUpgradeHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var report = await scope.ServiceProvider.GetRequiredService<LibraryUpgradeScanService>().RunAsync(stoppingToken);
                    if (report.Proposed > 0 || report.Failures > 0)
                    {
                        LogPass(report.Looked, report.Proposed, report.Failures);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Library upgrade scan looked at {Looked} books and proposed {Proposed} (failures: {Failures}).")]
    private partial void LogPass(int looked, int proposed, int failures);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Library upgrade scan failed.")]
    private partial void LogFailed(Exception exception);
}
