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
            DateTimeOffset.UtcNow, Guid.NewGuid());
        activities[requestFormatId] = activity;
        return new Lease(this, activity);
    }

    public sealed record Activity(
        Guid RequestId, Guid RequestFormatId, string ProviderId, string Stage, string? WorkTitle,
        DateTimeOffset StartedAtUtc, Guid LeaseId);

    public sealed class Lease(ActiveAcquisitionTracker tracker, Activity activity) : IDisposable
    {
        public void SetStage(string stage)
        {
            if (tracker.activities.TryGetValue(activity.RequestFormatId, out var current) && current.LeaseId == activity.LeaseId)
            {
                tracker.activities.TryUpdate(activity.RequestFormatId, current with { Stage = stage }, current);
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
