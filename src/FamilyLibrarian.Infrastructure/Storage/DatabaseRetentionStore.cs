using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Storage;

/// <summary>
/// One rule per table. A rule only removes rows that explain nothing still in use: a request that is open, a file that still exists,
/// the copy that was delivered to the library, an unresolved review and an identity-override record are always kept.
/// </summary>
public sealed class DatabaseRetentionStore(AppDbContext database) : IDatabaseRetentionStore
{
    public Task<int> PurgeAsync(RetentionTarget target, DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) => target switch
    {
        RetentionTarget.ArtifactEvidence => PurgeEvidenceAsync(cutoffUtc, batchSize, cancellationToken),
        RetentionTarget.SemanticFindings => database.SemanticFindings.Where(finding => finding.CreatedAtUtc < cutoffUtc)
            .OrderBy(finding => finding.CreatedAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken),
        RetentionTarget.ProviderAttempts => database.ProviderAttempts
            .Where(attempt => attempt.AttemptedAtUtc < cutoffUtc &&
                database.BookRequests.Any(request => request.Id == attempt.RequestId &&
                    (request.Status == RequestStatus.Available || request.Status == RequestStatus.Cancelled)))
            .OrderBy(attempt => attempt.AttemptedAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken),
        RetentionTarget.SearchVariantMemory => database.SearchVariantAttempts
            .Where(memory => memory.LastTriedAtUtc < cutoffUtc &&
                database.RequestFormats.Any(format => format.Id == memory.RequestFormatId &&
                    (format.Status == RequestFormatStatus.Available || format.Status == RequestFormatStatus.Cancelled)))
            .OrderBy(memory => memory.LastTriedAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken),
        RetentionTarget.Notifications => PurgeNotificationsAsync(cutoffUtc, batchSize, cancellationToken),
        RetentionTarget.InteractionAlerts => database.ProviderInteractionAlerts
            .Where(alert => (alert.State == ProviderInteractionAlertState.Resolved || alert.State == ProviderInteractionAlertState.Superseded ||
                    alert.State == ProviderInteractionAlertState.Expired) && (alert.ClosedAtUtc ?? alert.CreatedAtUtc) < cutoffUtc)
            .OrderBy(alert => alert.CreatedAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken),
        RetentionTarget.Communications => database.OutboundCommunications
            .Where(message => message.ProcessedAtUtc != null && message.ProcessedAtUtc < cutoffUtc)
            .OrderBy(message => message.CreatedAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken),
        RetentionTarget.AuditEvents => database.AuditEvents.Where(entry => entry.OccurredAtUtc < cutoffUtc)
            .OrderBy(entry => entry.OccurredAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown retention target.")
    };

    private Task<int> PurgeEvidenceAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) =>
        database.ArtifactEvidenceItems
            .Where(item => item.RecordKind != ArtifactEvidenceRecordKinds.IdentityOverride &&
                database.MediaAssets.Any(asset => asset.Id == item.MediaAssetId &&
                    (asset.StorageState == MediaAssetStorageState.Destroyed ||
                        // An archived file is the copy in the library. Its evidence is how a later pass recognises that the copy is
                        // incomplete and keeps looking for a better one, so it stays for as long as that copy is the delivered one.
                        // It expires only once a newer delivery of the same format has been confirmed in its place.
                        (asset.StorageState == MediaAssetStorageState.Archived &&
                            database.MediaAssets.Any(newer => newer.AssociatedRequestFormatId == asset.AssociatedRequestFormatId &&
                                newer.StorageState == MediaAssetStorageState.Archived && newer.CreatedAtUtc > asset.CreatedAtUtc &&
                                (newer.BundleId == null || newer.BundleId != asset.BundleId)))) &&
                    asset.UpdatedAtUtc < cutoffUtc &&
                    // Evidence behind a review that is still open explains the question being asked, so it stays.
                    !database.RequestFormats.Any(format => format.Id == asset.AssociatedRequestFormatId &&
                        database.BookRequests.Any(request => request.Id == format.RequestId && request.Status == RequestStatus.NeedsReview))))
            .OrderBy(item => item.RecordedAtUtc).Take(batchSize).ExecuteDeleteAsync(cancellationToken);

    private async Task<int> PurgeNotificationsAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken)
    {
        var ids = await database.NotificationEvents.Where(item => item.LastOccurredAtUtc < cutoffUtc)
            .OrderBy(item => item.LastOccurredAtUtc).Select(item => item.Id).Take(batchSize).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }

        await database.NotificationReceipts.Where(receipt => ids.Contains(receipt.NotificationEventId)).ExecuteDeleteAsync(cancellationToken);
        return await database.NotificationEvents.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
