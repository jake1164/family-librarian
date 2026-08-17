using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

public interface IAcquisitionRepository
{
    /// <summary>Used for manual-import duplicate detection.</summary>
    Task<bool> ExistsAssetWithChecksumForFormatAsync(
        Guid requestFormatId,
        string sha256,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every non-delivered <see cref="MediaAsset"/>, including rejected and
    /// removed records retained for administrator review and audit.
    /// </summary>
    Task<IReadOnlyList<MediaAssetAdminView>> ListActiveAsync(CancellationToken cancellationToken);

    void AddJob(AcquisitionJob job);

    void AddAsset(MediaAsset asset);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
