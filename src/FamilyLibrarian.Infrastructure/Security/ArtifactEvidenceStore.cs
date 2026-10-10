using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Security;

public sealed class ArtifactEvidenceStore(AppDbContext database) : IArtifactEvidenceStore
{
    public void Add(IReadOnlyList<ArtifactEvidence> evidence) => database.ArtifactEvidenceItems.AddRange(evidence);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Evidence is best effort. Detach what could not be saved so a failure here can never
            // poison a later save of an unrelated decision made in the same unit of work.
            foreach (var entry in database.ChangeTracker.Entries<ArtifactEvidence>()
                         .Where(entry => entry.State == EntityState.Added)
                         .ToArray())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    public async Task<string?> FindAcquisitionProviderIdAsync(
        Guid? acquisitionCandidateId, CancellationToken cancellationToken) =>
        acquisitionCandidateId is not { } candidateId
            ? null
            : await database.AcquisitionCandidates
                .Where(candidate => candidate.Id == candidateId)
                .Select(candidate => candidate.ProviderId)
                .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
        IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<ArtifactEvidence>>();

        var rows = await database.ArtifactEvidenceItems
            .AsNoTracking()
            .Where(item => assetIds.Contains(item.MediaAssetId))
            .ToListAsync(cancellationToken);

        // Newest record of each kind per asset; older records stay in the table as history.
        return rows
            .GroupBy(item => item.MediaAssetId)
            .ToDictionary(
                assetGroup => assetGroup.Key,
                assetGroup => (IReadOnlyList<ArtifactEvidence>)assetGroup
                    .GroupBy(item => item.RecordKind)
                    .SelectMany(kindGroup =>
                    {
                        var latestRecord = kindGroup.OrderByDescending(item => item.RecordedAtUtc).First().RecordId;
                        return kindGroup.Where(item => item.RecordId == latestRecord);
                    })
                    .OrderBy(item => item.RecordKind, StringComparer.Ordinal)
                    .ThenBy(item => item.FindingId, StringComparer.Ordinal)
                    .ToArray());
    }
}
