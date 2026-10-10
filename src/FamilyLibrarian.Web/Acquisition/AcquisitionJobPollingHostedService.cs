using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Drives every durable <see cref="Domain.Acquisition.ProviderAcquisitionJob"/>
/// whose next poll has come due. Kept separate from
/// <see cref="AutomaticRequestFulfillmentHostedService"/> rather than folded
/// into its 2-minute sweep: a provider's own polling-cadence hint
/// (protocol v2 §8) commonly wants single-digit-second cadence, and a job's
/// due-ness is genuinely per-job (via <c>NextPollAtUtc</c>), not a fixed
/// sweep interval. A restart leaves due jobs in the database exactly where
/// they were, so the next host instance resumes them without any special
/// recovery step.
/// </summary>
public sealed partial class AcquisitionJobPollingHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AcquisitionJobPollingHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var polling = scope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>();
                var processed = await polling.ProcessDueAsync(stoppingToken);
                if (processed > 0)
                {
                    LogProcessedJobs(processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One bad pass (a transient provider outage, a single
                // malformed response) must not stop the host -- due jobs
                // remain due and are retried on the next pass.
                LogPollingPassFailed(exception);
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
        Message = "Polled {JobCount} due provider acquisition job(s).")]
    private partial void LogProcessedJobs(int jobCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Acquisition job polling pass failed.")]
    private partial void LogPollingPassFailed(Exception exception);
}
