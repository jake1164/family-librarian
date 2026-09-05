using FamilyLibrarian.Application.Abstractions;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Attempts delivery of queued communications through every currently enabled
/// outbound provider. Each provider is tried at most once per communication;
/// there is no retry loop here — a failed delivery is recorded and the
/// communication is still marked processed, since retry policy is a separate,
/// not-yet-made decision.
/// </summary>
public sealed class OutboundCommunicationDispatcher(
    IOutboundCommunicationStore store,
    IEnumerable<IOutboundCommunicationProvider> providers,
    IClock clock)
{
    private const int BatchSize = 25;

    /// <summary>Processes one batch of queued communications and returns how many were processed.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await store.GetUnprocessedBatchAsync(BatchSize, cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        foreach (var communication in pending)
        {
            foreach (var provider in providers)
            {
                bool enabled;
                try
                {
                    enabled = await provider.IsEnabledAsync(cancellationToken);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!enabled)
                {
                    continue;
                }

                try
                {
                    var result = await provider.SendAsync(communication, cancellationToken);
                    communication.RecordDelivery(provider.ProviderId, result.Succeeded, result.Error, clock.UtcNow);
                }
                catch (Exception exception)
                {
                    // One provider throwing must not stop the others, and must
                    // not stop the rest of the batch, from being attempted.
                    communication.RecordDelivery(provider.ProviderId, succeeded: false, exception.Message, clock.UtcNow);
                }
            }

            communication.MarkProcessed(clock.UtcNow);

            // Commit right after this communication is marked processed, not
            // once for the whole batch: a provider send is not transactional
            // with our database, so if the process is interrupted (a restart,
            // a crash) between a successful send and the commit, a batched
            // commit would leave the row looking unprocessed and the next
            // dispatch pass would resend mail that already went out.
            await store.SaveChangesAsync(cancellationToken);
        }

        return pending.Count;
    }
}
