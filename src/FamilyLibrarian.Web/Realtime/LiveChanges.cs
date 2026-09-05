using FamilyLibrarian.Contracts.Realtime;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Gutenberg;
using FamilyLibrarian.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Realtime;

internal sealed class LiveChanges
{
    public LiveUpdateTopics AdminTopics { get; set; }
    public LiveUpdateTopics SharedTopics { get; set; }
    public HashSet<Guid> RequestIds { get; } = [];
    public HashSet<Guid> FormatIds { get; } = [];
    public HashSet<Guid> AssetIds { get; } = [];
    public HashSet<Guid> BundleIds { get; } = [];
    public HashSet<Guid> EvaluationIds { get; } = [];
    public HashSet<Guid> JobIds { get; } = [];
    public Dictionary<Guid, LiveUpdateTopics> UserTopics { get; } = [];
    public bool RevalidateConnections { get; set; }
    public bool HasChanges => RevalidateConnections || AdminTopics != LiveUpdateTopics.None ||
        SharedTopics != LiveUpdateTopics.None || UserTopics.Count > 0;

    public void ForUser(Guid userId, LiveUpdateTopics topics) =>
        UserTopics[userId] = UserTopics.GetValueOrDefault(userId) | topics;

    public void Merge(LiveChanges other)
    {
        AdminTopics |= other.AdminTopics;
        SharedTopics |= other.SharedTopics;
        RevalidateConnections |= other.RevalidateConnections;
        RequestIds.UnionWith(other.RequestIds);
        FormatIds.UnionWith(other.FormatIds);
        AssetIds.UnionWith(other.AssetIds);
        BundleIds.UnionWith(other.BundleIds);
        EvaluationIds.UnionWith(other.EvaluationIds);
        JobIds.UnionWith(other.JobIds);
        foreach (var (user, topics) in other.UserTopics)
        {
            ForUser(user, topics);
        }
    }

    public static LiveChanges Capture(DbContext context)
    {
        var changes = new LiveChanges();
        foreach (var entry in context.ChangeTracker.Entries().Where(entry =>
                     entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            switch (entry.Entity)
            {
                case AppUser:
                case IdentityUserRole<Guid>:
                    changes.RevalidateConnections = true;
                    break;
                case BookRequest request:
                    changes.RequestIds.Add(request.Id);
                    changes.ForUser(request.UserId, LiveUpdateTopics.Requests);
                    break;
                case RequestParticipant participant:
                    changes.RequestIds.Add(participant.RequestId);
                    changes.ForUser(participant.UserId, LiveUpdateTopics.Requests);
                    break;
                case RequestFormat format:
                    changes.RequestIds.Add(format.RequestId);
                    break;
                case RequestStatusHistory history:
                    changes.RequestIds.Add(history.RequestId);
                    break;
                case ProviderAttempt attempt:
                    changes.RequestIds.Add(attempt.RequestId);
                    break;
                case AcquisitionJob job:
                    changes.RequestIds.Add(job.RequestId);
                    break;
                case AcquisitionCandidate candidate:
                    changes.JobIds.Add(candidate.AcquisitionJobId);
                    break;
                case MediaAsset asset:
                    changes.FormatIds.Add(asset.AssociatedRequestFormatId);
                    changes.AdminTopics |= LiveUpdateTopics.Security;
                    break;
                case SecurityEvaluation evaluation:
                    changes.AssetIds.Add(evaluation.AssetId);
                    changes.AdminTopics |= LiveUpdateTopics.Security;
                    break;
                case SecurityScanResult scan:
                    changes.EvaluationIds.Add(scan.SecurityEvaluationId);
                    changes.AdminTopics |= LiveUpdateTopics.Security;
                    break;
                case FormatValidationResult validation:
                    changes.EvaluationIds.Add(validation.SecurityEvaluationId);
                    changes.AdminTopics |= LiveUpdateTopics.Security;
                    break;
                case Approval approval:
                    changes.EvaluationIds.Add(approval.SecurityEvaluationId);
                    changes.AdminTopics |= LiveUpdateTopics.Security;
                    break;
                case LibraryImport import:
                    changes.AssetIds.Add(import.AssetId);
                    changes.AdminTopics |= LiveUpdateTopics.Publishing;
                    break;
                case Delivery delivery:
                    if (delivery.AssetId is { } assetId) changes.AssetIds.Add(assetId);
                    if (delivery.BundleId is { } bundleId) changes.BundleIds.Add(bundleId);
                    changes.AdminTopics |= LiveUpdateTopics.Publishing;
                    break;
                case NotificationEvent notification when notification.Audience == NotificationAudience.AdminBroadcast:
                    changes.AdminTopics |= LiveUpdateTopics.Notifications;
                    break;
                case NotificationEvent notification when notification.RecipientUserId is { } userId:
                    changes.ForUser(userId, LiveUpdateTopics.Notifications);
                    break;
                case NotificationReceipt receipt:
                    changes.ForUser(receipt.UserId, LiveUpdateTopics.Notifications);
                    break;
                case GutenbergCatalogSyncStateEntity:
                case ProviderSetting:
                case ExternalProvider:
                    changes.AdminTopics |= LiveUpdateTopics.Sources | LiveUpdateTopics.Requests;
                    changes.SharedTopics |= LiveUpdateTopics.System;
                    break;
                case CwaSettings:
                case AudiobookshelfSettings:
                    changes.AdminTopics |= LiveUpdateTopics.Publishing;
                    changes.SharedTopics |= LiveUpdateTopics.System;
                    break;
            }
        }

        if (changes.RequestIds.Count + changes.JobIds.Count + changes.FormatIds.Count +
            changes.AssetIds.Count + changes.BundleIds.Count + changes.EvaluationIds.Count > 0)
        {
            changes.AdminTopics |= LiveUpdateTopics.Requests;
        }

        return changes;
    }
}
