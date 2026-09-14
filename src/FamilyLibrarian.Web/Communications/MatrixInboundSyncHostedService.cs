using FamilyLibrarian.Application.Communications;

namespace FamilyLibrarian.Web.Communications;

/// <summary>
/// Drives <see cref="MatrixInboundSyncCoordinator"/> (COMM-1 §D). The
/// coordinator's own Matrix <c>/sync</c> call long-polls the homeserver
/// itself, so there is no fixed poll interval here -- only a short backoff
/// after a failed pass, mirroring <c>OutboundCommunicationDispatcherHostedService</c>'s
/// "never let a transient provider or database problem stop the host" posture.
/// </summary>
public sealed partial class MatrixInboundSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MatrixInboundSyncHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleBackoff;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<MatrixInboundSyncCoordinator>();
                var processed = await coordinator.PollOnceAsync(stoppingToken);
                if (processed > 0)
                {
                    LogRouted(processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A temporary homeserver or database problem must not stop the
                // host. The next pass resumes from the last persisted cursor.
                LogSyncPassFailed(exception);
                delay = FailureBackoff;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
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
        Message = "Routed {MessageCount} inbound Matrix messages.")]
    private partial void LogRouted(int messageCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Matrix inbound sync pass failed.")]
    private partial void LogSyncPassFailed(Exception exception);
}
