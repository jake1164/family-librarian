using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Storage;

/// <summary>A6-STORAGE-1 S-2: every retention rule, decided on a snapshot so no disk or database is needed.</summary>
[TestClass]
public sealed class StorageRetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly StorageRetentionOptions Defaults = new();

    private static RemovableCopy Copy(MediaAssetStorageState state, RequestMediaType type, double ageDays, bool ruledOut = false, long bytes = 100,
        Guid? bundle = null, Guid? request = null, bool siblingTrusted = false, bool awaitingDecision = false) =>
        new(Guid.NewGuid(), bundle, state, type, ruledOut, bytes, Now.AddDays(-ageDays), request, "A Book", siblingTrusted, awaitingDecision);

    private static IReadOnlyList<PlannedRemoval> Removals(IReadOnlyList<RemovableCopy> copies, bool pressure = false, StorageRetentionOptions? options = null) =>
        StorageRetentionPlanner.Plan(copies, options ?? Defaults, Now, pressure).Removals;

    [TestMethod]
    [DataRow(13.9, false)]
    [DataRow(14.0, true)]
    public void RejectedFilesGoAfterFourteenDays(double age, bool removed) =>
        Assert.AreEqual(removed, Removals([Copy(MediaAssetStorageState.Rejected, RequestMediaType.Ebook, age)]).Any(r => r.Reason == RemovalReason.Rejected));

    [TestMethod]
    [DataRow(6.9, false)]
    [DataRow(7.0, true)]
    public void CopiesTheSearchRuledOutGoAfterSevenDays(double age, bool removed) =>
        Assert.AreEqual(removed, Removals([Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, age, ruledOut: true)]).Any(r => r.Reason == RemovalReason.RuledOut));

    [TestMethod]
    [DataRow(RequestMediaType.Ebook, 29.9, false)]
    [DataRow(RequestMediaType.Ebook, 30.0, true)]
    [DataRow(RequestMediaType.Audiobook, 13.9, false)]
    [DataRow(RequestMediaType.Audiobook, 14.0, true)]
    public void CopiesWaitingForAPersonKeepTheirOwnLimitPerKind(RequestMediaType type, double age, bool removed) =>
        Assert.AreEqual(removed, Removals([Copy(MediaAssetStorageState.Unmatched, type, age)]).Any(r => r.Reason == RemovalReason.WaitingExpired));

    [TestMethod]
    [DataRow(MediaAssetStorageState.Quarantine)]
    [DataRow(MediaAssetStorageState.Processing)]
    public void FilesStuckBeforeTrustGoAfterSevenDaysButNotSooner(MediaAssetStorageState state)
    {
        Assert.IsEmpty(Removals([Copy(state, RequestMediaType.Ebook, 6.9)]));
        Assert.AreEqual(RemovalReason.Stuck, Removals([Copy(state, RequestMediaType.Ebook, 7.0)]).Single().Reason);
    }

    [TestMethod]
    [DataRow(RequestMediaType.Ebook, 29.9, false)]
    [DataRow(RequestMediaType.Ebook, 30.0, true)]
    [DataRow(RequestMediaType.Audiobook, 13.9, false)]
    [DataRow(RequestMediaType.Audiobook, 14.0, true)]
    public void ACheckedFileWaitingForALibrarianIsNotStuckAndKeepsTheWaitingLimit(RequestMediaType type, double age, bool removed)
    {
        var removals = Removals([Copy(MediaAssetStorageState.Processing, type, age, awaitingDecision: true)]);

        Assert.IsFalse(removals.Any(r => r.Reason == RemovalReason.Stuck), "A finished check waiting for a person is never 'stuck'.");
        Assert.AreEqual(removed, removals.Any(r => r.Reason == RemovalReason.WaitingExpired));
        Assert.IsTrue(removals.All(r => r.WasWaitingForPerson && r.State == MediaAssetStorageState.Processing));
    }

    [TestMethod]
    public void ACheckedFileWaitingForALibrarianIsRemindedCountsTowardTheCapAndIsNotRushedByALowDisk()
    {
        var request = Guid.NewGuid();
        var waiting = Copy(MediaAssetStorageState.Processing, RequestMediaType.Ebook, 15.0, request: request, awaitingDecision: true);

        var plan = StorageRetentionPlanner.Plan([waiting], Defaults, Now, underPressure: true);
        Assert.IsEmpty(plan.Removals, "A low disk never shortens the wait for a person.");
        Assert.AreEqual(Now.AddDays(15), plan.Reminders.Single(r => r.RequestId == request).RemovalDateUtc);

        var big = Copy(MediaAssetStorageState.Processing, RequestMediaType.Audiobook, 2, bytes: 11 * StorageRetentionOptions.Gigabyte, awaitingDecision: true);
        Assert.AreEqual(RemovalReason.HeldCap, Removals([big]).Single().Reason, "It is bytes waiting for a person, so it counts toward the size limit.");
    }

    [TestMethod]
    public void ACheckedFileTheSearchMovedPastGoesOnTheRuledOutTimer()
    {
        Assert.IsEmpty(Removals([Copy(MediaAssetStorageState.Processing, RequestMediaType.Ebook, 6.9, ruledOut: true, awaitingDecision: true)]));
        Assert.AreEqual(RemovalReason.RuledOut,
            Removals([Copy(MediaAssetStorageState.Processing, RequestMediaType.Ebook, 7.0, ruledOut: true, awaitingDecision: true)]).Single().Reason);
    }

    [TestMethod]
    public void OneTrackWaitingForALibrarianKeepsTheWholeSetOffTheStuckTimer()
    {
        var bundle = Guid.NewGuid();
        var set = new[]
        {
            Copy(MediaAssetStorageState.Processing, RequestMediaType.Audiobook, 9, bundle: bundle, awaitingDecision: true),
            Copy(MediaAssetStorageState.Processing, RequestMediaType.Audiobook, 9, bundle: bundle)
        };

        Assert.IsEmpty(Removals(set), "Removing the unfinished track would break the copy a person is deciding on.");
    }

    [TestMethod]
    [DataRow(MediaAssetStorageState.Processing, null, false)]
    [DataRow(MediaAssetStorageState.Processing, SecurityEvaluationStatus.Pending, false)]
    [DataRow(MediaAssetStorageState.Processing, SecurityEvaluationStatus.Failed, false)]
    [DataRow(MediaAssetStorageState.Processing, SecurityEvaluationStatus.Passed, true)]
    [DataRow(MediaAssetStorageState.Processing, SecurityEvaluationStatus.ReviewRequired, true)]
    [DataRow(MediaAssetStorageState.Quarantine, SecurityEvaluationStatus.Passed, false)]
    public void OnlyAFinishedCheckInProcessingIsWaitingForADecision(MediaAssetStorageState state, SecurityEvaluationStatus? latest, bool expected) =>
        Assert.AreEqual(expected, StorageRetentionPlanner.IsAwaitingDecision(state, latest));

    [TestMethod]
    public void WhatIsKeptIsNeverARemovalOfATrustedFile()
    {
        // The store never lists trusted files; a set that has a trusted track is skipped even when its held tracks are very old.
        var bundle = Guid.NewGuid();
        var removals = Removals([Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 365, bundle: bundle, siblingTrusted: true),
            Copy(MediaAssetStorageState.Rejected, RequestMediaType.Audiobook, 365, bundle: bundle, siblingTrusted: true)]);

        Assert.IsEmpty(removals, "A set that is partly in the library may be the only copy of what was delivered.");
    }

    [TestMethod]
    public void ASetIsRemovedAsOneAndItsNewestTrackDecidesItsAge()
    {
        var bundle = Guid.NewGuid();
        var old = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 40, bundle: bundle);
        var fresh = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 1, bundle: bundle);
        Assert.IsEmpty(Removals([old, fresh]), "One recent track means the set is still being worked on.");

        var both = Removals([old, Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 20, bundle: bundle)]);

        var removal = both.Single();
        Assert.HasCount(2, removal.AssetIds, "Never half a set: a set with a track removed is useless.");
    }

    [TestMethod]
    public void DiskPressureShortensRejectedAndRuledOutCopiesOnly()
    {
        var rejected = Copy(MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 1.5);
        var ruledOut = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 1.5, ruledOut: true);
        var waitingBook = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 5);
        var stuck = Copy(MediaAssetStorageState.Quarantine, RequestMediaType.Ebook, 3);
        var all = new[] { rejected, ruledOut, waitingBook, stuck };

        Assert.IsEmpty(Removals(all, pressure: false));
        var underPressure = Removals(all, pressure: true);

        CollectionAssert.AreEquivalent(new[] { rejected.AssetId, ruledOut.AssetId }, underPressure.SelectMany(r => r.AssetIds).ToArray());
        Assert.IsFalse(underPressure.Any(r => r.AssetIds.Contains(waitingBook.AssetId)), "A copy waiting for a person may be the only copy: never hurried.");
    }

    [TestMethod]
    public void TheHeldSizeCapRemovesAudiobooksFirstThenOldestUntilItFits()
    {
        const long Gb = StorageRetentionOptions.Gigabyte;
        var options = new StorageRetentionOptions { HeldBytesCap = 10 * Gb };
        var oldBook = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 20, bytes: 1 * Gb);
        var newAudio = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 2, bytes: 6 * Gb);
        var oldAudio = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 9, bytes: 6 * Gb);

        var removals = Removals([oldBook, newAudio, oldAudio], options: options);

        var removed = removals.Single();
        Assert.AreEqual(RemovalReason.HeldCap, removed.Reason);
        CollectionAssert.AreEqual(new[] { oldAudio.AssetId }, removed.AssetIds.ToArray(), "13 GB held, cap 10: the oldest audiobook alone brings it to 7.");
    }

    [TestMethod]
    public void TheCapKeepsRemovingOldestFirstUntilTheTotalFits()
    {
        const long Gb = StorageRetentionOptions.Gigabyte;
        var a = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 3, bytes: 6 * Gb);
        var b = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 6, bytes: 6 * Gb);
        var c = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 9, bytes: 6 * Gb);

        var removed = Removals([a, b, c], options: new StorageRetentionOptions { HeldBytesCap = 10 * Gb }).SelectMany(r => r.AssetIds).ToArray();

        CollectionAssert.AreEquivalent(new[] { b.AssetId, c.AssetId }, removed, "18 GB held, cap 10: two oldest go, leaving 6.");
    }

    [TestMethod]
    public void TheCapNeverCountsOrRemovesCopiesTheSearchAlreadyRuledOutOrFilesNotWaiting()
    {
        const long Gb = StorageRetentionOptions.Gigabyte;
        var ruledOut = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 1, ruledOut: true, bytes: 50 * Gb);
        var rejected = Copy(MediaAssetStorageState.Rejected, RequestMediaType.Audiobook, 1, bytes: 50 * Gb);

        Assert.IsEmpty(Removals([ruledOut, rejected]));
    }

    [TestMethod]
    public void ReminderAppearsOnceHalfWayWithTheRemovalDate()
    {
        var request = Guid.NewGuid();
        var young = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 14.9, request: Guid.NewGuid());
        var half = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 15.0, request: request);
        var audioHalf = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, 7.0, request: Guid.NewGuid());

        var reminders = StorageRetentionPlanner.Plan([young, half, audioHalf], Defaults, Now, underPressure: false).Reminders;

        Assert.HasCount(2, reminders);
        Assert.AreEqual(Now.AddDays(15), reminders.Single(r => r.RequestId == request).RemovalDateUtc);
    }

    [TestMethod]
    public void AFileBeingRemovedIsNotAlsoReminded()
    {
        var expired = Copy(MediaAssetStorageState.Unmatched, RequestMediaType.Ebook, 31, request: Guid.NewGuid());

        var plan = StorageRetentionPlanner.Plan([expired], Defaults, Now, underPressure: false);

        Assert.HasCount(1, plan.Removals);
        Assert.IsEmpty(plan.Reminders);
    }

    [TestMethod]
    public void OneRunIsBoundedAndTakesTheOldestFirst()
    {
        var options = new StorageRetentionOptions { BatchSize = 3 };
        var copies = Enumerable.Range(0, 10).Select(i => Copy(MediaAssetStorageState.Rejected, RequestMediaType.Ebook, 20 + i)).ToArray();

        var removals = Removals(copies, options: options);

        Assert.HasCount(3, removals);
        CollectionAssert.AreEquivalent(copies.OrderBy(c => c.UpdatedAtUtc).Take(3).Select(c => c.AssetId).ToArray(), removals.SelectMany(r => r.AssetIds).ToArray());
    }

    [TestMethod]
    public void ConfiguredValuesCannotDisableCleanUpOrMakeItAggressive()
    {
        var wild = new StorageRetentionOptions
        {
            RejectedDays = 0, RuledOutDays = -5, WaitingEbookDays = 1, WaitingAudiobookDays = 0, StuckDays = 0, HeldBytesCap = 1,
            OrphanGraceHours = 0, BatchSize = 100000
        }.Clamped();

        Assert.AreEqual(1, wild.RejectedDays);
        Assert.AreEqual(1, wild.RuledOutDays);
        Assert.AreEqual(7, wild.WaitingEbookDays, "A person always gets at least a week for an ebook.");
        Assert.AreEqual(3, wild.WaitingAudiobookDays);
        Assert.AreEqual(2, wild.StuckDays);
        Assert.AreEqual(StorageRetentionOptions.Gigabyte, wild.HeldBytesCap);
        Assert.AreEqual(6, wild.OrphanGraceHours, "A download in flight is never swept.");
        Assert.AreEqual(500, wild.BatchSize);
        var huge = new StorageRetentionOptions { RejectedDays = 9999, WaitingEbookDays = 9999, HeldBytesCap = long.MaxValue }.Clamped();
        Assert.AreEqual(90, huge.RejectedDays);
        Assert.AreEqual(90, huge.WaitingEbookDays);
        Assert.AreEqual(100 * StorageRetentionOptions.Gigabyte, huge.HeldBytesCap);
    }

    [TestMethod]
    [DataRow(100L, 9L, true)]
    [DataRow(100L, 10L, false)]
    [DataRow(1000L, 99L, true)]
    [DataRow(1000L, 100L, false)]
    public void TheDiskFloorIsTenPercentOnALargeDisk(long totalGb, long freeGb, bool pressure)
    {
        var volume = new StorageVolumeInfo(freeGb * StorageRetentionOptions.Gigabyte, totalGb * StorageRetentionOptions.Gigabyte);

        Assert.AreEqual(pressure, StorageDiskFloor.IsUnderPressure(volume));
        Assert.AreEqual(totalGb / 10 * StorageRetentionOptions.Gigabyte, StorageDiskFloor.RequiredFreeBytes(volume));
    }

    [TestMethod]
    public void ASmallDiskUsesFiveGigabytesAndAnUnknownDiskIsNeverTreatedAsFull()
    {
        var small = new StorageVolumeInfo(4L * StorageRetentionOptions.Gigabyte, 20L * StorageRetentionOptions.Gigabyte);
        Assert.AreEqual(5L * StorageRetentionOptions.Gigabyte, StorageDiskFloor.RequiredFreeBytes(small));
        Assert.IsTrue(StorageDiskFloor.IsUnderPressure(small));
        Assert.IsFalse(StorageDiskFloor.IsUnderPressure(new StorageVolumeInfo(null, null)));
        Assert.IsFalse(StorageDiskFloor.IsUnderPressure(new StorageVolumeInfo(6L * StorageRetentionOptions.Gigabyte, 20L * StorageRetentionOptions.Gigabyte)));
    }

    [TestMethod]
    public void DownloadHeadroomIsNeverBelowTheFloorUnlessTheCheckIsTurnedOff()
    {
        const long Gb = StorageRetentionOptions.Gigabyte;
        var volume = new StorageVolumeInfo(50 * Gb, 200 * Gb);

        Assert.AreEqual(20 * Gb, StorageDiskFloor.Headroom(1 * Gb, volume), "The 1 GB default is lifted to 10% of a 200 GB disk.");
        Assert.AreEqual(30 * Gb, StorageDiskFloor.Headroom(30 * Gb, volume), "A larger configured headroom stands.");
        Assert.AreEqual(0, StorageDiskFloor.Headroom(0, volume), "Zero is the operator's deliberate off switch.");
        Assert.AreEqual(5 * Gb, StorageDiskFloor.Headroom(1 * Gb, new StorageVolumeInfo(50 * Gb, null)), "Unknown total: the 5 GB minimum.");
    }
}
