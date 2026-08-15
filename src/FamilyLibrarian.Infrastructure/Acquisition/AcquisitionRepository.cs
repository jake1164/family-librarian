using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class AcquisitionRepository(AppDbContext database) : IAcquisitionRepository
{
    public Task<bool> ExistsAssetWithChecksumForFormatAsync(
        Guid requestFormatId,
        string sha256,
        CancellationToken cancellationToken) =>
        database.MediaAssets.AnyAsync(
            asset => asset.AssociatedRequestFormatId == requestFormatId && asset.Sha256 == sha256,
            cancellationToken);

    public void AddJob(AcquisitionJob job) => database.AcquisitionJobs.Add(job);

    public void AddAsset(MediaAsset asset) => database.MediaAssets.Add(asset);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);
}
