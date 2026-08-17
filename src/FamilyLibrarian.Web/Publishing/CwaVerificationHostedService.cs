using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Publishing;

/// <summary>
/// Confirms CWA handoffs after CWA has had time to ingest them. A confirmation
/// is an OPDS read only; it cannot send a second copy of a book.
/// </summary>
public sealed partial class CwaVerificationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CwaVerificationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var publishing = scope.ServiceProvider.GetRequiredService<CwaPublishingService>();
                var checkedCount = await publishing.RecheckAwaitingVerificationAsync(stoppingToken);
                if (checkedCount > 0)
                {
                    LogRecheckedImports(checkedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Verification is advisory: a temporary CWA/OPDS outage must
                // not take down the host or re-send an approved book.
                LogVerificationPassFailed(exception);
            }

            try
            {
                await Task.Delay(VerificationInterval, stoppingToken);
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
        Message = "Rechecked {ImportCount} CWA imports awaiting verification.")]
    private partial void LogRecheckedImports(int importCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "CWA import verification pass failed.")]
    private partial void LogVerificationPassFailed(Exception exception);
}
