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
    /// Every <see cref="MediaAsset"/> still in <c>Quarantine</c> or
    /// <c>Processing</c> — the set that needs an administrator's attention.
    /// </summary>
    Task<IReadOnlyList<MediaAssetAdminView>> ListActiveAsync(CancellationToken cancellationToken);

    void AddJob(AcquisitionJob job);

    void AddAsset(MediaAsset asset);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
