using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Runs <see cref="ProviderInteractionAlertService.RunPassAsync"/> on a fixed
/// interval — the HUMAN-ACQ-1 counterpart of <c>OutboundCommunicationDispatcherHostedService</c>.
/// </summary>
public sealed partial class ProviderInteractionAlertHostedService(
    IServiceScopeFactory scopeFactory,
    ProviderInteractionAlertOptions options,
    ILogger<ProviderInteractionAlertHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var alertService = scope.ServiceProvider.GetRequiredService<ProviderInteractionAlertService>();
                await alertService.RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A temporary provider, database, or Matrix problem must not stop
                // the host. Nothing here is lost: the next pass re-evaluates every
                // waiting job and open alert from scratch.
                LogPassFailed(exception);
            }

            try
            {
                await Task.Delay(options.PassInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Warning,
        Message = "Provider interaction alert pass failed.")]
    private partial void LogPassFailed(Exception exception);
}
