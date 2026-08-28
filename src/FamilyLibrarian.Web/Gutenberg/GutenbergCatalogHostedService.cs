using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Gutenberg;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Web.Gutenberg;

/// <summary>Runs the local Project Gutenberg RDF import once each day without delaying host startup.</summary>
public sealed partial class GutenbergCatalogHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<GutenbergCatalogOptions> options,
    TimeProvider timeProvider,
    ILogger<GutenbergCatalogHostedService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var synchronizer = scope.ServiceProvider.GetRequiredService<IGutenbergCatalogSynchronizer>();
                var catalog = scope.ServiceProvider.GetRequiredService<IGutenbergCatalog>();
                var status = await catalog.GetStatusAsync(stoppingToken);
                if (!status.IsReady)
                {
                    await synchronizer.SynchronizeAsync(stoppingToken);
                }

                var delay = NextScheduledRun(timeProvider.GetUtcNow()) - timeProvider.GetUtcNow();
                await Task.Delay(delay, stoppingToken);
                await synchronizer.SynchronizeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCatalogPassFailed(exception);
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
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
