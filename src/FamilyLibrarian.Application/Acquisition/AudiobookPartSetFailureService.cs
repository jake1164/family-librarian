using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Sends a request to a librarian. Implemented by <see cref="AutomaticRequestFulfillmentService"/>.</summary>
public interface IPartSetReviewRouter
{
    Task SendToReviewAsync(Guid requestId, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Abandons an automatic audiobook set when one part cannot be completed: the
/// other parts stop downloading, every part already staged is destroyed, and
/// the request goes to a librarian naming the failed part. Nothing from a
/// failed set is ever left behind to be published or to confuse a later retry.
/// </summary>
public sealed class AudiobookPartSetFailureService(
    IProviderAcquisitionJobStore jobs,
    ISecurityEvaluationRepository assets,
    IAssetStagingStore stagingStore,
    IPartSetReviewRouter reviews,
    IAuditWriter audit,
    IClock clock)
{
    public async Task FailAsync(ProviderAcquisitionJob failedMember, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedMember);
        if (failedMember.PartSetId is not { } setId)
        {
            return;
        }

        var summary = $"Part {failedMember.PartNumber} of {failedMember.PartTotal} of the audiobook set " +
            $"could not be completed: {reason}";

        foreach (var member in await jobs.ListByPartSetAsync(setId, cancellationToken))
        {
            member.Cancel(summary, clock.UtcNow);
            foreach (var assetId in member.Outputs.Where(output => output.MediaAssetId.HasValue)
                         .Select(output => output.MediaAssetId!.Value))
            {
                await DiscardAsync(assetId, setId, cancellationToken);
            }
        }

        await jobs.SaveChangesAsync(cancellationToken);
        await reviews.SendToReviewAsync(failedMember.RequestId, summary, cancellationToken);
    }

    private async Task DiscardAsync(Guid assetId, Guid setId, CancellationToken cancellationToken)
    {
        var asset = await assets.FindAssetAsync(assetId, cancellationToken);
        if (asset is null || asset.StorageState is MediaAssetStorageState.Trusted or
                MediaAssetStorageState.Archived or MediaAssetStorageState.Destroyed)
        {
            return;
        }

        try
        {
            await stagingStore.DeleteAsync(asset.StorageState, asset.StoredFilename, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Already gone; the database row still has to reflect that.
        }

        asset.TransitionStorageState(MediaAssetStorageState.Destroyed, clock.UtcNow);
        await assets.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.AssetPartSetDiscarded,
            AuditSubjectTypes.MediaAsset,
            assetId.ToString(),
            new { AssetId = assetId, SetId = setId },
            cancellationToken);
    }
}
