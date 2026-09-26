using System.Collections.Concurrent;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Transient, host-local activity for synchronous acquisition work.</summary>
public sealed class ActiveAcquisitionTracker
{
    private readonly ConcurrentDictionary<Guid, Activity> activities = new();

    public IReadOnlyList<Activity> Snapshot() => activities.Values.OrderBy(activity => activity.StartedAtUtc).ToArray();

    public Lease Begin(Guid requestId, Guid requestFormatId, string providerId, string stage, string? workTitle = null)
    {
        var activity = new Activity(requestId, requestFormatId, providerId, stage, workTitle,
            DateTimeOffset.UtcNow, null, 0, null, 0, Guid.NewGuid());
        activities[requestFormatId] = activity;
        return new Lease(this, activity);
    }

    public sealed record Activity(
        Guid RequestId, Guid RequestFormatId, string ProviderId, string Stage, string? WorkTitle,
        DateTimeOffset StartedAtUtc, DateTimeOffset? TransferStartedAtUtc,
        long BytesReceived, long? TotalBytes, long TransferStartBytesReceived, Guid LeaseId);

    public sealed class Lease(ActiveAcquisitionTracker tracker, Activity activity) : IDisposable
    {
        public void SetStage(string stage)
        {
            if (tracker.activities.TryGetValue(activity.RequestFormatId, out var current) && current.LeaseId == activity.LeaseId)
            {
                var transferStarted = stage == "Downloading" ? DateTimeOffset.UtcNow : current.TransferStartedAtUtc;
                tracker.activities.TryUpdate(activity.RequestFormatId,
                    current with { Stage = stage, TransferStartedAtUtc = transferStarted }, current);
            }
        }

        public void ReportTransferProgress(long bytesReceived, long? totalBytes, string stage, bool isTransferBaseline = false)
        {
            if (tracker.activities.TryGetValue(activity.RequestFormatId, out var current) && current.LeaseId == activity.LeaseId)
            {
                var updated = current with
                {
                    Stage = stage,
                    TransferStartedAtUtc = isTransferBaseline || current.TransferStartedAtUtc is null
                        ? DateTimeOffset.UtcNow
                        : current.TransferStartedAtUtc,
                    BytesReceived = Math.Max(0, bytesReceived),
                    TotalBytes = totalBytes is >= 0 ? totalBytes : current.TotalBytes,
                    TransferStartBytesReceived = isTransferBaseline
                        ? Math.Max(0, bytesReceived)
                        : current.TransferStartBytesReceived
                };
                tracker.activities.TryUpdate(activity.RequestFormatId, updated, current);
            }
        }

        public void Dispose()
        {
            if (tracker.activities.TryGetValue(activity.RequestFormatId, out var current) && current.LeaseId == activity.LeaseId)
            {
                ((ICollection<KeyValuePair<Guid, Activity>>)tracker.activities)
                    .Remove(new KeyValuePair<Guid, Activity>(activity.RequestFormatId, current));
            }
        }
    }
}
