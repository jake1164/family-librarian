using FamilyLibrarian.Application.Storage;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>Trims old database rows once a day so an unattended installation keeps a small database.</summary>
public sealed partial class DatabaseRetentionHostedService(IServiceScopeFactory scopeFactory, ILogger<DatabaseRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
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
                    var report = await scope.ServiceProvider.GetRequiredService<DatabaseRetentionService>().RunAsync(stoppingToken);
                    if (report.TotalRemoved > 0 || report.Failures > 0)
                    {
                        LogPass(report.TotalRemoved, report.Failures);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Database retention removed {Removed} old rows (table failures: {Failures}).")]
    private partial void LogPass(int removed, int failures);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Database retention pass failed.")]
    private partial void LogFailed(Exception exception);
}
