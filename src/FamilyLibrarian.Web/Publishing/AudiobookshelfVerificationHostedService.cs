using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Publishing;

/// <summary>
/// Confirms Audiobookshelf handoffs after Audiobookshelf has had time to index
/// them. A confirmation is an API read only; it cannot send a second copy of
/// a book. Mirrors <see cref="CwaVerificationHostedService"/> -- without this,
/// a delivery that misses its one immediate post-upload check stays
/// "Verifying" forever, unlike a CWA import, which this service's CWA
/// counterpart keeps retrying automatically.
/// </summary>
public sealed partial class AudiobookshelfVerificationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AudiobookshelfVerificationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var publishing = scope.ServiceProvider.GetRequiredService<AudiobookshelfPublishingService>();
                var checkedCount = await publishing.RecheckAwaitingVerificationAsync(stoppingToken);
                if (checkedCount > 0)
                {
                    LogRecheckedDeliveries(checkedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Verification is advisory: a temporary Audiobookshelf outage
                // must not take down the host or re-send an approved book.
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
        Message = "Rechecked {DeliveryCount} Audiobookshelf deliveries awaiting verification.")]
    private partial void LogRecheckedDeliveries(int deliveryCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Audiobookshelf delivery verification pass failed.")]
    private partial void LogVerificationPassFailed(Exception exception);
}
