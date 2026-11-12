using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>Reads the exact durable job whose staged output is this asset, including after a restart.</summary>
public sealed class ArtifactPeerProfileSource(AppDbContext database) : IArtifactPeerProfileSource
{
    public Task<PeerSizeEvidence?> FindAsync(MediaAsset asset, CancellationToken cancellationToken) =>
        database.ProviderAcquisitionJobs.AsNoTracking()
            .Where(job => job.IsAutomaticAcquisition && job.RequestFormatId == asset.AssociatedRequestFormatId &&
                job.PeerMedianSizeBytes > 0 && job.PeerCount >= 3 && job.Outputs.Any(output => output.MediaAssetId == asset.Id))
            .Select(job => new PeerSizeEvidence(job.PeerMedianSizeBytes!.Value, job.PeerCount!.Value))
            .SingleOrDefaultAsync(cancellationToken);
}
