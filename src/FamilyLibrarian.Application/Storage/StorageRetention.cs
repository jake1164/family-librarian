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
        RejectedDays = MaintenanceLimits.Clamp(RejectedDays, MaintenanceLimits.RejectedDays),
        RuledOutDays = MaintenanceLimits.Clamp(RuledOutDays, MaintenanceLimits.RuledOutDays),
        WaitingEbookDays = MaintenanceLimits.Clamp(WaitingEbookDays, MaintenanceLimits.WaitingEbookDays),
        WaitingAudiobookDays = MaintenanceLimits.Clamp(WaitingAudiobookDays, MaintenanceLimits.WaitingAudiobookDays),
        StuckDays = MaintenanceLimits.Clamp(StuckDays, MaintenanceLimits.StuckDays),
        HeldBytesCap = Math.Clamp(HeldBytesCap, MaintenanceLimits.HeldCapGb.Min * Gigabyte, MaintenanceLimits.HeldCapGb.Max * Gigabyte),
        OrphanGraceHours = MaintenanceLimits.Clamp(OrphanGraceHours, MaintenanceLimits.OrphanGraceHours),
        BatchSize = Math.Clamp(BatchSize, 1, 500),
        PressureDays = MaintenanceLimits.Clamp(PressureDays, MaintenanceLimits.PressureDays)
    };
}

/// <summary>
/// The smallest and largest value each setting may take. One place, used by the clamping in the options records and by the settings screen,
/// so what the screen says is allowed is exactly what is enforced.
/// </summary>
public static class MaintenanceLimits
{
    public static readonly (int Min, int Max) RejectedDays = (1, 90);
    public static readonly (int Min, int Max) RuledOutDays = (1, 30);
    public static readonly (int Min, int Max) WaitingEbookDays = (7, 90);
    public static readonly (int Min, int Max) WaitingAudiobookDays = (3, 60);
    public static readonly (int Min, int Max) StuckDays = (2, 30);
    public static readonly (int Min, int Max) HeldCapGb = (1, 100);
    public static readonly (int Min, int Max) OrphanGraceHours = (6, 168);
    public static readonly (int Min, int Max) PressureDays = (1, 7);
    public static readonly (int Min, int Max) EvidenceDays = (30, 3650);
    public static readonly (int Min, int Max) SemanticFindingDays = (30, 3650);
    public static readonly (int Min, int Max) AttemptDays = (30, 3650);
    public static readonly (int Min, int Max) SearchMemoryDays = (7, 3650);
    public static readonly (int Min, int Max) NotificationDays = (14, 3650);
    public static readonly (int Min, int Max) AlertDays = (14, 3650);
    public static readonly (int Min, int Max) CommunicationDays = (14, 3650);
    public static readonly (int Min, int Max) AuditDays = (365, 3650);
    public static readonly (int Min, int Max) UpgradeScanIntervalDays = (7, 365);

    public static int Clamp(int value, (int Min, int Max) limit) => Math.Clamp(value, limit.Min, limit.Max);
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

    /// <summary>
    /// When the age rule will remove a copy that has been in its state since <paramref name="stateSinceUtc"/>; null for a state clean-up
    /// never touches. It is the latest date by age: the size limit or a low disk can remove a held copy sooner, and releasing it keeps it.
    /// </summary>
    public static DateTimeOffset? RemovalDate(
        MediaAssetStorageState state, RequestMediaType type, bool ruledOut, DateTimeOffset stateSinceUtc, StorageRetentionOptions options)
    {
        var o = options.Clamped();
        return state switch
        {
            MediaAssetStorageState.Rejected => stateSinceUtc + Days(o.RejectedDays),
            MediaAssetStorageState.Quarantine or MediaAssetStorageState.Processing => stateSinceUtc + Days(o.StuckDays),
            MediaAssetStorageState.Unmatched when ruledOut => stateSinceUtc + Days(o.RuledOutDays),
            MediaAssetStorageState.Unmatched => stateSinceUtc + Days(WaitingDays(type, o)),
            _ => null
        };
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
