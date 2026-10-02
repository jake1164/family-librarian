using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Lets the code that rules out a failed copy wake the background fulfillment
/// worker, so the next candidate starts at once instead of at the next sweep.
/// </summary>
/// <remarks>
/// A binary signal on purpose: any number of requests made while a pass is
/// already running collapse into one further pass, which is all that is ever
/// useful because every pass re-reads what is due. Losing a signal is harmless
/// -- the worker's periodic sweep still runs -- so there is no queue, no
/// ordering and no failure mode beyond a slower retry.
/// </remarks>
public sealed class AutomaticFulfillmentSignal : IAutomaticFulfillmentSignal, IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Request()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the pending wake-up covers this request too.
        }
    }

    /// <summary>
    /// Waits for the next signal or for <paramref name="timeout"/>, whichever
    /// comes first. Returns <see langword="true"/> when woken by a signal.
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => signal.Dispose();
}
