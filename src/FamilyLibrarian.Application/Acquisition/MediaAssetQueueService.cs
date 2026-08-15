using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Read-only composition of the active acquisition/security queue: every
/// <see cref="MediaAssetAdminView"/> that still needs an administrator's
/// attention, paired with its latest <see cref="SecurityEvaluation"/> if one
/// has run yet.
/// </summary>
/// <remarks>
/// One evaluation lookup per asset rather than a single joined query — the
/// queue is expected to hold a handful of items at household scale, and this
/// keeps the query on each side simple rather than fighting EF Core's
/// correlated-subquery translation for a multi-child aggregate.
/// </remarks>
public sealed class MediaAssetQueueService(
    IAcquisitionRepository acquisition,
    ISecurityEvaluationRepository security)
{
    public async Task<IReadOnlyList<MediaAssetQueueEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var assets = await acquisition.ListActiveAsync(cancellationToken);
        var entries = new List<MediaAssetQueueEntry>(assets.Count);
        foreach (var asset in assets)
        {
            var evaluation = await security.FindLatestEvaluationAsync(asset.AssetId, cancellationToken);
            entries.Add(new MediaAssetQueueEntry(asset, evaluation));
        }

        return entries;
    }
}

public sealed record MediaAssetQueueEntry(MediaAssetAdminView Asset, SecurityEvaluation? LatestEvaluation);
