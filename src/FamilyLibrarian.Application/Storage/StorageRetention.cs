using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Storage;

/// <summary>
/// How long unwanted files are kept. Defaults are the owner-agreed values; <see cref="Clamped"/> keeps any configured value inside
/// fixed ceilings so a typo cannot turn clean-up off or make it aggressive.
/// </summary>
public sealed record StorageRetentionOptions
{
    public const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>Files that failed a security check. Nothing needs them; they are kept only so a librarian can see what happened.</summary>
    public int RejectedDays { get; init; } = 14;

    /// <summary>Held copies the automatic search has already moved past: no person needs them.</summary>
    public int RuledOutDays { get; init; } = 7;

    /// <summary>Held ebooks waiting for a person. Small, so kept longest.</summary>
    public int WaitingEbookDays { get; init; } = 30;

    /// <summary>Held audiobooks waiting for a person. Large, so kept for less time.</summary>
    public int WaitingAudiobookDays { get; init; } = 14;

    /// <summary>Received or being checked for this long means the check will never finish.</summary>
    public int StuckDays { get; init; } = 7;

    /// <summary>Total size of copies waiting for a person; beyond it the oldest go first (audiobooks before ebooks).</summary>
    public long HeldBytesCap { get; init; } = 10 * Gigabyte;

    /// <summary>A file with no record is only called an orphan after this long, so a download in flight is never touched.</summary>
    public int OrphanGraceHours { get; init; } = 24;

    /// <summary>Most files or sets removed in one pass, so a big backlog cannot monopolise the host.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Under disk pressure the rejected and ruled-out copies are kept only this long (never the copies waiting for a person).</summary>
    public int PressureDays { get; init; } = 1;

    public StorageRetentionOptions Clamped() => this with
    {
        RejectedDays = Math.Clamp(RejectedDays, 1, 90),
        RuledOutDays = Math.Clamp(RuledOutDays, 1, 30),
        WaitingEbookDays = Math.Clamp(WaitingEbookDays, 7, 90),
        WaitingAudiobookDays = Math.Clamp(WaitingAudiobookDays, 3, 60),
        StuckDays = Math.Clamp(StuckDays, 2, 30),
        HeldBytesCap = Math.Clamp(HeldBytesCap, Gigabyte, 100 * Gigabyte),
        OrphanGraceHours = Math.Clamp(OrphanGraceHours, 6, 168),
        BatchSize = Math.Clamp(BatchSize, 1, 500),
        PressureDays = Math.Clamp(PressureDays, 1, 7)
    };
}

/// <summary>The disk safety floor: the larger of 5 GB or 10% of the volume.</summary>
public static class StorageDiskFloor
{
    public const long MinimumBytes = 5L * StorageRetentionOptions.Gigabyte;

    public static long RequiredFreeBytes(StorageVolumeInfo volume) =>
        Math.Max(MinimumBytes, volume.TotalBytes is { } total ? total / 10 : 0);

    /// <summary>
    /// Free space a download must leave behind: the configured headroom, but never less than the safety floor. A configured zero still
    /// means "do not check", which is how an operator turns the pre-download check off deliberately.
    /// </summary>
    public static long Headroom(long configuredMinimumBytes, StorageVolumeInfo volume) =>
        configuredMinimumBytes > 0 ? Math.Max(configuredMinimumBytes, RequiredFreeBytes(volume)) : 0;

    /// <summary>True only when free space is known and below the floor; an unknown volume is never treated as full.</summary>
    public static bool IsUnderPressure(StorageVolumeInfo volume) => volume.FreeBytes is { } free && free < RequiredFreeBytes(volume);
}

/// <summary>One file held on disk that clean-up might remove. Never carries a name or path.</summary>
/// <param name="SiblingIsTrusted">A track of the same set is already trusted or archived: the set is partly in the library, so it is left alone.</param>
public sealed record RemovableCopy(
    Guid AssetId, Guid? BundleId, MediaAssetStorageState State, RequestMediaType MediaType, bool RuledOut, long Bytes,
    DateTimeOffset UpdatedAtUtc, Guid? RequestId, string? WorkTitle, bool SiblingIsTrusted);

public enum RemovalReason
{
    Rejected,
    RuledOut,
    WaitingExpired,
    HeldCap,
    Stuck
}

/// <summary>A file, or a whole multi-track set, to remove together.</summary>
public sealed record PlannedRemoval(RemovalReason Reason, IReadOnlyList<Guid> AssetIds, MediaAssetStorageState State, long Bytes, Guid? RequestId, string? WorkTitle)
{
    public bool WasWaitingForPerson => Reason is RemovalReason.WaitingExpired or RemovalReason.HeldCap;
}

public sealed record PlannedReminder(Guid RequestId, string WorkTitle, DateTimeOffset RemovalDateUtc);

public sealed record RetentionPlan(IReadOnlyList<PlannedRemoval> Removals, IReadOnlyList<PlannedReminder> Reminders);

/// <summary>Decides what to remove from a snapshot. Pure, so every rule is testable without a disk or database.</summary>
public static class StorageRetentionPlanner
{
    public static RetentionPlan Plan(IReadOnlyList<RemovableCopy> copies, StorageRetentionOptions options, DateTimeOffset now, bool underPressure)
    {
        var o = options.Clamped();
        var removals = new List<PlannedRemoval>();
        var waiting = new List<Group>();

        foreach (var group in Groups(copies))
        {
            // A set with a track already in the library may be the only copy of what was delivered.
            if (group.Members.Any(member => member.SiblingIsTrusted))
            {
                continue;
            }

            var age = now - group.Newest;
            switch (group.State)
            {
                case MediaAssetStorageState.Rejected:
                    if (age >= Days(underPressure ? Math.Min(o.RejectedDays, o.PressureDays) : o.RejectedDays))
                    {
                        removals.Add(group.ToRemoval(RemovalReason.Rejected));
                    }

                    break;
                case MediaAssetStorageState.Quarantine or MediaAssetStorageState.Processing:
                    if (age >= Days(o.StuckDays))
                    {
                        removals.Add(group.ToRemoval(RemovalReason.Stuck));
                    }

                    break;
                case MediaAssetStorageState.Unmatched when group.RuledOut:
                    if (age >= Days(underPressure ? Math.Min(o.RuledOutDays, o.PressureDays) : o.RuledOutDays))
                    {
                        removals.Add(group.ToRemoval(RemovalReason.RuledOut));
                    }

                    break;
                case MediaAssetStorageState.Unmatched:
                    // Waiting for a person: never sooner because the disk is tight; the cap below is the only size-based rule.
                    if (age >= Days(WaitingDays(group.MediaType, o)))
                    {
                        removals.Add(group.ToRemoval(RemovalReason.WaitingExpired));
                    }
                    else
                    {
                        waiting.Add(group);
                    }

                    break;
            }
        }

        // Size cap: audiobooks first (large), then ebooks, oldest first within each, until the rest fits.
        var total = waiting.Sum(group => group.Bytes);
        foreach (var group in waiting.OrderBy(group => group.MediaType == RequestMediaType.Audiobook ? 0 : 1).ThenBy(group => group.Newest))
        {
            if (total <= o.HeldBytesCap)
            {
                break;
            }

            removals.Add(group.ToRemoval(RemovalReason.HeldCap));
            total -= group.Bytes;
        }

        var removed = removals.SelectMany(removal => removal.AssetIds).ToHashSet();
        var reminders = new List<PlannedReminder>();
        foreach (var group in waiting.Where(group => group.RequestId is not null && !group.Members.Any(member => removed.Contains(member.AssetId))))
        {
            var limit = Days(WaitingDays(group.MediaType, o));
            if (now - group.Newest >= limit / 2)
            {
                reminders.Add(new PlannedReminder(group.RequestId!.Value, group.WorkTitle ?? "a book", group.Newest + limit));
            }
        }

        // Oldest first, so a capped batch always makes progress on the longest-waiting files.
        var ordered = removals.OrderBy(removal => copies.Where(copy => removal.AssetIds.Contains(copy.AssetId)).Max(copy => copy.UpdatedAtUtc)).Take(o.BatchSize).ToArray();
        return new RetentionPlan(ordered, [.. reminders.DistinctBy(reminder => reminder.RequestId)]);
    }

    public static int WaitingDays(RequestMediaType type, StorageRetentionOptions options) =>
        type == RequestMediaType.Audiobook ? options.WaitingAudiobookDays : options.WaitingEbookDays;

    private static TimeSpan Days(int days) => TimeSpan.FromDays(days);

    private static IEnumerable<Group> Groups(IReadOnlyList<RemovableCopy> copies) =>
        copies.GroupBy(copy => (Key: copy.BundleId ?? copy.AssetId, copy.State, copy.RuledOut))
            .Select(group => new Group(group.Key.State, group.Key.RuledOut, [.. group]));

    private sealed record Group(MediaAssetStorageState State, bool RuledOut, IReadOnlyList<RemovableCopy> Members)
    {
        // The newest track decides the age: a set still being worked on is not stale because its first track is old.
        public DateTimeOffset Newest => Members.Max(member => member.UpdatedAtUtc);

        public long Bytes => Members.Sum(member => member.Bytes);

        public RequestMediaType MediaType => Members[0].MediaType;

        public Guid? RequestId => Members[0].RequestId;

        public string? WorkTitle => Members[0].WorkTitle;

        public PlannedRemoval ToRemoval(RemovalReason reason) =>
            new(reason, [.. Members.Select(member => member.AssetId)], State, Bytes, RequestId, WorkTitle);
    }
}
