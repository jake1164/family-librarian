namespace FamilyLibrarian.Application.Catalog;

/// <summary>Ebook and audiobook <see cref="FulfillmentOption"/>s found for a raw catalog search candidate.</summary>
public sealed record CandidateAvailabilityResult(
    IReadOnlyList<FulfillmentOption> Ebook, IReadOnlyList<FulfillmentOption> Audiobook);

/// <summary>
/// The identity-based sibling of <see cref="IWorkFulfillmentOptionsService"/>:
/// checks whether the household already owns or can acquire a raw search
/// candidate, without requiring it to be a persisted Work first. The
/// implementation (<c>FamilyLibrarian.Infrastructure.Catalog.CandidateAvailabilityService</c>)
/// lives in Infrastructure rather than here, alongside every other DI
/// -container-aware component -- unlike <see cref="WorkFulfillmentOptionsService"/>'s
/// sequential fan-out, it runs every provider call concurrently, and each
/// concurrent call needs its own dependency-injection scope (so its own
/// <c>DbContext</c>), which is a container concern this layer otherwise
/// stays free of.
/// </summary>
public interface ICandidateAvailabilityService
{
    Task<CandidateAvailabilityResult> GetAvailabilityAsync(BookIdentity identity, CancellationToken cancellationToken);
}
