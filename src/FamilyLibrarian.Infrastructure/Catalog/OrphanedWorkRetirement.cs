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
/// rather than deleted because the cancelled requests still reference it, and
/// its provider references are removed so a new request can create a fresh
/// Work under the same provider id (that pair is unique).
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
        if (orphans.Count == 0)
        {
            return 0;
        }

        var orphanIds = orphans.Select(work => work.Id).ToArray();
        var references = await database.ExternalReferences
            .Where(reference => reference.EntityType == ExternalReferenceEntityType.Work &&
                orphanIds.Contains(reference.EntityId))
            .ToListAsync(cancellationToken);
        database.ExternalReferences.RemoveRange(references);
        foreach (var work in orphans)
        {
            work.Retire(now);
        }

        await database.SaveChangesAsync(cancellationToken);
        return orphans.Count;
    }
}
