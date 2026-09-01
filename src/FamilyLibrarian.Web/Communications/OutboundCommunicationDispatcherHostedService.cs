using FamilyLibrarian.Application.Communications;

namespace FamilyLibrarian.Web.Communications;

/// <summary>
/// Polls for queued communications and attempts delivery through every
/// enabled outbound provider, independently of the request that queued them.
/// </summary>
public sealed partial class OutboundCommunicationDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboundCommunicationDispatcherHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboundCommunicationDispatcher>();
                var processed = await dispatcher.DispatchPendingAsync(stoppingToken);
                if (processed > 0)
                {
                    LogDispatched(processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A temporary provider or database problem must not stop the
                // host. Queued communications remain unprocessed and are
                // retried on the next pass.
                LogDispatchPassFailed(exception);
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
        Message = "Dispatched {CommunicationCount} queued communications.")]
    private partial void LogDispatched(int communicationCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Outbound communication dispatch pass failed.")]
    private partial void LogDispatchPassFailed(Exception exception);
}
