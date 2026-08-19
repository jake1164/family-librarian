using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Processes newly submitted requests independently from the browser request.
/// A restart leaves the request pending, so the next host instance safely picks
/// it up instead of relying on a fire-and-forget task tied to an HTTP request.
/// </summary>
public sealed partial class AutomaticRequestFulfillmentHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AutomaticRequestFulfillmentHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var fulfillment = scope.ServiceProvider.GetRequiredService<AutomaticRequestFulfillmentService>();
                var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
                var processed = await fulfillment.ProcessPendingAsync(stoppingToken);
                var checkedProviders = await rechecks.ProcessDueAsync(stoppingToken);
                if (processed > 0)
                {
                    LogProcessedRequests(processed);
                }
                if (checkedProviders > 0)
                {
                    LogCheckedProviders(checkedProviders);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A temporary provider or destination problem must not stop the
                // host. The request remains pending and will be retried after a
                // restart or processed normally on the next pass.
                LogProcessingPassFailed(exception);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Automatically acquired {RequestFormatCount} requested book formats.")]
    private partial void LogProcessedRequests(int requestFormatCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Checked {ProviderLookupCount} scheduled external provider lookups.")]
    private partial void LogCheckedProviders(int providerLookupCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Automatic request-fulfillment pass failed.")]
    private partial void LogProcessingPassFailed(Exception exception);
}
