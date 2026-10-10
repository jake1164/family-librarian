namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Asks the background fulfillment worker to run a pass now instead of waiting
/// out its sweep interval.
/// </summary>
/// <remarks>
/// Used when a failed copy has just been ruled out and the next candidate is
/// ready to try: without it the replacement waited for the next two-minute
/// sweep, which made the retry loop look stalled. Purely a latency hint --
/// every consumer is optional and the periodic sweep remains the guarantee, so
/// a missed or absent signal costs time, never correctness.
/// </remarks>
public interface IAutomaticFulfillmentSignal
{
    void Request();
}
