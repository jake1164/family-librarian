using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Upgrades;

public sealed class LibraryUpgradeStore(AppDbContext database) : ILibraryUpgradeStore
{
    private static readonly LibraryUpgradeState[] OpenStates =
        [LibraryUpgradeState.Proposed, LibraryUpgradeState.Acquiring, LibraryUpgradeState.Publishing, LibraryUpgradeState.Verified];

    private static readonly MediaAssetStorageState[] DeliveredStates = [MediaAssetStorageState.Archived, MediaAssetStorageState.Trusted];

    public async Task<IReadOnlyList<UpgradeSubject>> ListSubjectsAsync(DateTimeOffset notScannedSince, int maximumCount, CancellationToken cancellationToken)
    {
        var codes = DeficientCopyDetector.CodesThatMakeACopyDeficient.ToArray();
        var candidates = await (
            from format in database.RequestFormats.AsNoTracking()
            where format.Status == RequestFormatStatus.Available &&
                (format.UpgradeScannedAtUtc == null || format.UpgradeScannedAtUtc < notScannedSince) &&
                !database.LibraryUpgrades.Any(upgrade => upgrade.RequestFormatId == format.Id && OpenStates.Contains(upgrade.State)) &&
                database.MediaAssets.Any(asset => asset.AssociatedRequestFormatId == format.Id && DeliveredStates.Contains(asset.StorageState) &&
                    database.ArtifactEvidenceItems.Any(item => item.MediaAssetId == asset.Id && codes.Contains(item.Code) &&
                        (item.Polarity == EvidencePolarity.Condition || item.Polarity == EvidencePolarity.Negative)))
            orderby format.UpgradeScannedAtUtc
            select new { format.Id, format.RequestId, format.MediaType })
            .Take(maximumCount).ToListAsync(cancellationToken);

        var subjects = new List<UpgradeSubject>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var workId = await database.BookRequests.AsNoTracking().Where(request => request.Id == candidate.RequestId).Select(request => request.WorkId).SingleAsync(cancellationToken);
            var newest = await database.MediaAssets.AsNoTracking()
                .Where(asset => asset.AssociatedRequestFormatId == candidate.Id && DeliveredStates.Contains(asset.StorageState))
                .OrderByDescending(asset => asset.CreatedAtUtc).FirstAsync(cancellationToken);
            // The newest delivery is the copy in the library now; an older one that an upgrade replaced is not judged again.
            var deliveredIds = newest.BundleId is { } bundle
                ? await database.MediaAssets.AsNoTracking().Where(asset => asset.BundleId == bundle).Select(asset => asset.Id).ToListAsync(cancellationToken)
                : [newest.Id];
            var evidence = await database.ArtifactEvidenceItems.AsNoTracking().Where(item => deliveredIds.Contains(item.MediaAssetId))
                .Select(item => new { item.Dimension, item.Code, item.Polarity, item.RecordKind }).ToListAsync(cancellationToken);
            var known = (await database.ProviderAcquisitionJobs.AsNoTracking().Where(job => job.RequestFormatId == candidate.Id)
                    .Select(job => new KnownCandidate(job.ProviderId, job.CandidateReference, job.CandidateFingerprint)).ToListAsync(cancellationToken))
                .Concat(await database.LibraryUpgrades.AsNoTracking().Where(upgrade => upgrade.RequestFormatId == candidate.Id)
                    .Select(upgrade => new KnownCandidate(upgrade.ProviderId, upgrade.CandidateReference, upgrade.CandidateFingerprint)).ToListAsync(cancellationToken))
                .ToArray();
            subjects.Add(new UpgradeSubject(candidate.RequestId, candidate.Id, workId, candidate.MediaType, newest.Id,
                new DeliveredCopyFacts(newest.Origin, evidence.Any(item => item.RecordKind == ArtifactEvidenceRecordKinds.IdentityOverride),
                    [.. evidence.Select(item => new DeliveredEvidenceFact(item.Dimension, item.Code, item.Polarity))]), known));
        }

        return subjects;
    }

    public Task MarkScannedAsync(Guid requestFormatId, DateTimeOffset atUtc, CancellationToken cancellationToken) =>
        database.RequestFormats.Where(format => format.Id == requestFormatId)
            .ExecuteUpdateAsync(set => set.SetProperty(format => format.UpgradeScannedAtUtc, atUtc), cancellationToken);

    public void Add(LibraryUpgrade upgrade) => database.LibraryUpgrades.Add(upgrade);

    public Task<LibraryUpgrade?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.LibraryUpgrades.FirstOrDefaultAsync(upgrade => upgrade.Id == id, cancellationToken);

    public Task<LibraryUpgrade?> FindOpenForFormatAsync(Guid requestFormatId, CancellationToken cancellationToken) =>
        database.LibraryUpgrades.FirstOrDefaultAsync(upgrade => upgrade.RequestFormatId == requestFormatId && OpenStates.Contains(upgrade.State), cancellationToken);

    public async Task<IReadOnlyList<LibraryUpgrade>> ListByStateAsync(LibraryUpgradeState state, int maximumCount, CancellationToken cancellationToken) =>
        await database.LibraryUpgrades.Where(upgrade => upgrade.State == state).OrderBy(upgrade => upgrade.UpdatedAtUtc).Take(maximumCount).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LibraryUpgrade>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken) =>
        await database.LibraryUpgrades.AsNoTracking().OrderByDescending(upgrade => upgrade.CreatedAtUtc).Take(maximumCount).ToListAsync(cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
