using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

public interface IAcquisitionRepository
{
    /// <summary>Used for manual-import duplicate detection.</summary>
    Task<bool> ExistsAssetWithChecksumForFormatAsync(
        Guid requestFormatId,
        string sha256,
        CancellationToken cancellationToken);

    void AddJob(AcquisitionJob job);

    void AddAsset(MediaAsset asset);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
