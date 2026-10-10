using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Web.Providers;

/// <summary>
/// Periodically re-probes <c>/health</c> for every enabled external
/// provider, independent of each provider's own <c>RecheckSchedule</c> --
/// that field only governs candidate-lookup cadence and must not double as
/// "whether the provider is ever checked for reachability" (see
/// <see cref="ExternalProviderHealthPollService"/>'s own remarks). Mirrors
/// <see cref="Publishing.PublishingDestinationHealthHostedService"/>'s
/// scope-per-pass shape and interval exactly, for the same reason: a
/// provider container started alongside this host can still be mid-boot the
/// moment this host is ready to serve traffic.
/// </summary>
public sealed partial class ExternalProviderHealthHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ExternalProviderHealthHostedService> logger) : BackgroundService
{
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
                var poller = scope.ServiceProvider.GetRequiredService<ExternalProviderHealthPollService>();
                var checkedCount = await poller.CheckAllEnabledAsync(stoppingToken);
                if (checkedCount > 0)
                {
                    LogCheckedProviders(checkedCount);
                }
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

    [LoggerMessage(EventId = 930, Level = LogLevel.Information, Message = "external.provider.health.background.checked {ProviderCount}")]
    private partial void LogCheckedProviders(int providerCount);

    [LoggerMessage(EventId = 931, Level = LogLevel.Warning, Message = "external.provider.health.background.failed")]
    private partial void LogHealthCheckPassFailed(Exception exception);
}
