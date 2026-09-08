using FamilyLibrarian.Application.Delivery;

namespace FamilyLibrarian.Web.Publishing;

/// <summary>
/// Retries transient-failed Kindle delivery attempts. A narrow sweep over
/// Failed/retryable <c>DeliveryAttempt</c> rows only -- readiness itself is
/// event-driven from <c>CwaPublishingService</c>, not polled here. Same shape
/// as <see cref="CwaVerificationHostedService"/>, on a coarser interval since
/// delivery retry cooldowns are measured in minutes, not seconds.
/// </summary>
public sealed partial class DeliveryRetryHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<DeliveryRetryHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var deliveryAttempts = scope.ServiceProvider.GetRequiredService<DeliveryAttemptService>();
                var retriedCount = await deliveryAttempts.RetryFailedAsync(stoppingToken);
                if (retriedCount > 0)
                {
                    LogRetriedAttempts(retriedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Retry is advisory: a temporary failure here must not take
                // down the host or skip a later sweep.
                LogRetrySweepFailed(exception);
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Retried {AttemptCount} failed Kindle delivery attempts.")]
    private partial void LogRetriedAttempts(int attemptCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Kindle delivery retry sweep failed.")]
    private partial void LogRetrySweepFailed(Exception exception);
}
