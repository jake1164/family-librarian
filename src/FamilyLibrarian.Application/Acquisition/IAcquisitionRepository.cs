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
    /// Every non-delivered <see cref="MediaAsset"/> that still has staged
    /// bytes or needs attention. Destroyed records remain available only as
    /// audit evidence, not live security-queue work.
    /// </summary>
    Task<IReadOnlyList<MediaAssetAdminView>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>Recent staged-file activity, including completed scans and
    /// publishing handoffs, for the administrator's operational history.</summary>
    Task<IReadOnlyList<MediaAssetAdminView>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken);

    void AddJob(AcquisitionJob job);

    /// <summary>Received acquisitions over retained history, counted once per job regardless of file count.</summary>
    Task<IReadOnlyList<SourceDownloadStatistics>> ListSourceDownloadStatisticsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceDownloadStatistics>>([]);

    void AddAsset(MediaAsset asset);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record SourceDownloadStatistics(string ProviderId, long DownloadsReceived);
