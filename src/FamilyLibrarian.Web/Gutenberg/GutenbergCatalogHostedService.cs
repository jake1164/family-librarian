using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Infrastructure.Gutenberg;
using FamilyLibrarian.Infrastructure.Providers;
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
                await SynchronizeIfEnabledAsync(onlyWhenMissing: true, stoppingToken);

                var delay = NextScheduledRun(timeProvider.GetUtcNow()) - timeProvider.GetUtcNow();
                await Task.Delay(delay, stoppingToken);
                await SynchronizeIfEnabledAsync(onlyWhenMissing: false, stoppingToken);
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

    private async Task SynchronizeIfEnabledAsync(bool onlyWhenMissing, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IProviderRegistry>();
        var settings = scope.ServiceProvider.GetRequiredService<IProviderSettingsStore>();
        var descriptor = registry.Find(ProviderRegistry.GutenbergProviderId);
        if (descriptor is null || !ProviderState.IsEnabled(
                descriptor,
                await settings.FindAsync(descriptor.Id, cancellationToken)))
        {
            return;
        }

        var catalog = scope.ServiceProvider.GetRequiredService<IGutenbergCatalog>();
        var status = await catalog.GetStatusAsync(cancellationToken);
        if (onlyWhenMissing && status.IsReady)
        {
            return;
        }

        var synchronizer = scope.ServiceProvider.GetRequiredService<IGutenbergCatalogSynchronizer>();
        if (status.IsReady)
        {
            await synchronizer.SynchronizeIncrementalAsync(cancellationToken);
            return;
        }

        await synchronizer.SynchronizeAsync(cancellationToken);
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
