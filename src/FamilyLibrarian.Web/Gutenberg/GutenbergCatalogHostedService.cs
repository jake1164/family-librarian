using FamilyLibrarian.Infrastructure.Gutenberg;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Web.Gutenberg;

/// <summary>Runs the local Project Gutenberg RDF import once each day without delaying host startup.</summary>
internal sealed partial class GutenbergCatalogHostedService(
    GutenbergCatalogRefreshCoordinator coordinator,
    IOptions<GutenbergCatalogOptions> options,
    TimeProvider timeProvider,
    ILogger<GutenbergCatalogHostedService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await coordinator.RecoverInterruptedAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkMissing = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (checkMissing)
                {
                    await coordinator.QueueAsync(onlyWhenMissing: true, stoppingToken);
                    checkMissing = false;
                }
                await coordinator.RunQueuedAsync(stoppingToken);
                var delay = NextScheduledRun(timeProvider.GetUtcNow()) - timeProvider.GetUtcNow();
                if (!await coordinator.WaitForWorkAsync(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, stoppingToken))
                    await CheckForUpdatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCatalogPassFailed(exception);
                await coordinator.WaitForWorkAsync(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    internal async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        await coordinator.QueueAsync(onlyWhenMissing: false, cancellationToken);
        await coordinator.RunQueuedAsync(cancellationToken);
    }

    private DateTimeOffset NextScheduledRun(DateTimeOffset now)
    {
        var easternNow = TimeZoneInfo.ConvertTime(now, EasternTimeZone);
        var candidateDate = DateOnly.FromDateTime(easternNow.DateTime);
        if (easternNow.Hour >= options.Value.SyncHourEastern)
        {
            candidateDate = candidateDate.AddDays(1);
        }

        var unspecified = candidateDate.ToDateTime(new TimeOnly(options.Value.SyncHourEastern, 0));
        var offset = EasternTimeZone.GetUtcOffset(unspecified);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(unspecified, offset), TimeZoneInfo.Utc);
    }

    [LoggerMessage(EventId = 903, Level = LogLevel.Warning, Message = "gutenberg.catalog.background.failed")]
    private partial void LogCatalogPassFailed(Exception exception);
}
