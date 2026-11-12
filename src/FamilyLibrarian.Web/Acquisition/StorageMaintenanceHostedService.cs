using FamilyLibrarian.Application.Storage;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>Runs storage clean-up every few hours so an unattended installation does not fill its disk.</summary>
public sealed partial class StorageMaintenanceHostedService(IServiceScopeFactory scopeFactory, ILogger<StorageMaintenanceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the host finish starting (and any in-flight downloads register) before the first pass.
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var report = await scope.ServiceProvider.GetRequiredService<MaintenanceCycle>().RunStorageAsync(stoppingToken);
                    if (report.TotalRemoved > 0 || report.UnderDiskPressure || report.Failures > 0)
                    {
                        LogPass(report.TotalRemoved, report.UnderDiskPressure, report.Failures);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Storage clean-up removed {Removed} files (disk pressure: {Pressure}, step failures: {Failures}).")]
    private partial void LogPass(int removed, bool pressure, int failures);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Storage clean-up pass failed.")]
    private partial void LogFailed(Exception exception);
}
