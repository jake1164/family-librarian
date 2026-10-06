using System.Collections.Concurrent;
using System.Threading.Channels;
using FamilyLibrarian.Application.Security;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Hands a manual security (re)scan to a background worker instead of running
/// it inside the HTTP request that asked for it.
/// </summary>
/// <remarks>
/// A scan of a large audiobook takes minutes. Run inside the request, it dies
/// with the request — a closed tab, a proxy idle timeout, or an app restart
/// cancels it halfway. Queued, it runs to completion regardless, and the admin
/// page follows progress through the SignalR live updates every save already
/// publishes (see <c>LiveChanges</c>). Scans run one at a time so a burst of
/// retries cannot hammer ClamAV.
/// </remarks>
public sealed class SecurityEvaluationScheduler
{
    private readonly ConcurrentDictionary<Guid, byte> pending = [];
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <returns><c>false</c> when this asset is already queued or being scanned.</returns>
    public bool TryEnqueue(Guid assetId)
    {
        if (!pending.TryAdd(assetId, 0))
        {
            return false;
        }

        if (queue.Writer.TryWrite(assetId))
        {
            return true;
        }

        pending.TryRemove(assetId, out _);
        return false;
    }

    internal IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        queue.Reader.ReadAllAsync(cancellationToken);

    internal void MarkFinished(Guid assetId) => pending.TryRemove(assetId, out _);
}

public sealed partial class SecurityEvaluationHostedService(
    SecurityEvaluationScheduler queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SecurityEvaluationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var assetId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<AutomatedSecurityPipeline>();
                var result = await pipeline.EvaluateAsync(assetId, stoppingToken);
                if (result.Outcome != SecurityEvaluationOutcome.Success)
                {
                    LogNotEvaluated(assetId, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down. SecurityEvaluationService has already returned
                // the asset to Quarantine, so it can be retried after restart.
            }
            catch (Exception exception)
            {
                // SecurityEvaluationService records the reason on the asset
                // before rethrowing; this is the operator-facing trace.
                LogEvaluationFailed(exception, assetId);
            }
            finally
            {
                queue.MarkFinished(assetId);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Security evaluation of media asset {AssetId} failed.")]
    private partial void LogEvaluationFailed(Exception exception, Guid assetId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Queued security evaluation of media asset {AssetId} did not run: {Reason}")]
    private partial void LogNotEvaluated(Guid assetId, string? reason);
}
