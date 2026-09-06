using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Publishing;

/// <summary>
/// Periodically re-tests CWA and Audiobookshelf against their currently saved
/// configuration when enabled, so <c>LastTestSucceeded</c> (and so the status
/// footer, the Settings menu badges, and the per-destination status chip on
/// the Publishing settings page) reflects reality instead of a one-off result
/// from whenever an admin last clicked "Test connection" -- including a
/// result recorded during a destination's own startup window that a later,
/// successful boot never gets the chance to correct on its own. Mirrors
/// <see cref="Gutenberg.GutenbergCatalogHostedService"/>'s scope-per-pass shape.
/// </summary>
public sealed partial class PublishingDestinationHealthHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PublishingDestinationHealthHostedService> logger) : BackgroundService
{
    // Deliberately not immediate: a destination container started alongside
    // this host (e.g. the same Compose `up`) can still be mid-boot the moment
    // this host is ready to serve traffic -- see toontown-int-srv2's
    // Audiobookshelf result, recorded four minutes after container start with
    // "Connection refused" against a container that came up healthy minutes
    // later. Waiting out that window before the first check avoids recording
    // the same kind of stale failure this service exists to correct.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(InitialDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await CheckCwaAsync(scope.ServiceProvider, stoppingToken);
                await CheckAudiobookshelfAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A failed health-check pass is itself just a recorded test
                // failure on the next successful pass; it must not take down
                // the host.
                LogHealthCheckPassFailed(exception);
            }

            if (!await DelayAsync(CheckInterval, stoppingToken))
            {
                break;
            }
        }
    }

    private static async Task CheckCwaAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<ICwaSettingsStore>();
        var settings = await store.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true })
        {
            return;
        }

        var service = services.GetRequiredService<CwaSettingsService>();
        await service.TestConnectionAsync(CwaConnectionTestTarget.All, cancellationToken);
    }

    private static async Task CheckAudiobookshelfAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IAudiobookshelfSettingsStore>();
        var settings = await store.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true })
        {
            return;
        }

        var service = services.GetRequiredService<AudiobookshelfSettingsService>();
        await service.TestConnectionAsync(cancellationToken);
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 920, Level = LogLevel.Warning, Message = "publishing.destination.health.background.failed")]
    private partial void LogHealthCheckPassFailed(Exception exception);
}
