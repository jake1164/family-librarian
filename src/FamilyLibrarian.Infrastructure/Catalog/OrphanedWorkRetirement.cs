using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Catalog;

/// <summary>
/// Retires Works that exist only because of cancelled requests, so a stale or
/// wrong provider snapshot is not reused forever.
/// </summary>
/// <remarks>
/// A Work earns its reuse by carrying shared state: a live request, an acquired
/// file, or someone's feedback. A Work whose every request was cancelled and
/// that has none of those is just a cached provider record. It is retired
/// rather than deleted because the cancelled requests still reference it. Its
/// provider references and editions are removed, because both are unique
/// catalog-wide (provider id, ISBN) and would block a new request from
/// creating a fresh Work for the same book.
/// </remarks>
public sealed class OrphanedWorkRetirement(AppDbContext database)
{
    public async Task<int> RetireAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var orphans = await database.Works
            .Where(work => !work.IsRetired &&
                database.BookRequests.Any(request => request.WorkId == work.Id) &&
                !database.BookRequests.Any(request =>
                    request.WorkId == work.Id && request.Status != RequestStatus.Cancelled) &&
                !database.MediaAssets.Any(asset => asset.WorkId == work.Id) &&
                !database.UserWorkFeedback.Any(feedback => feedback.WorkId == work.Id))
            .ToListAsync(cancellationToken);
        foreach (var work in orphans)
        {
            work.Retire(now);
        }

        await database.SaveChangesAsync(cancellationToken);

        // Every retired Work must let go of what is unique catalog-wide, including
        // one retired by an earlier version of this cleanup that released only
        // the provider reference. Edition ISBNs are unique, so a retired Work that
        // kept one would stop a fresh Work for the same book from saving. Nothing
        // else references an edition.
        var references = await database.ExternalReferences
            .Where(reference => reference.EntityType == ExternalReferenceEntityType.Work &&
                database.Works.Any(work => work.Id == reference.EntityId && work.IsRetired))
            .ToListAsync(cancellationToken);
        var editions = await database.Editions
            .Where(edition => database.Works.Any(work => work.Id == edition.WorkId && work.IsRetired))
            .ToListAsync(cancellationToken);
        database.ExternalReferences.RemoveRange(references);
        database.Editions.RemoveRange(editions);
        await database.SaveChangesAsync(cancellationToken);

        return orphans.Count;
    }
}
