using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Acquisition;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Storage;

/// <summary>A6-STORAGE-1 S-1: the installation can see what it holds and what uses the disk, without deleting anything or exposing a name.</summary>
[TestClass]
public sealed class StorageInsightTests
{
    private static readonly string[] BigMidSmall = ["big", "mid", "small"];
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : FamilyLibrarian.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeStaging : IAssetStagingStore
    {
        public Dictionary<MediaAssetStorageState, List<StoredFileInfo>> Files { get; } = [];

        public StorageVolumeInfo Volume { get; set; } = new(50L * 1024 * 1024 * 1024, 200L * 1024 * 1024 * 1024);

        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MoveAsync(MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IReadOnlyList<StoredFileInfo> ListStoredFiles(MediaAssetStorageState zone) => Files.GetValueOrDefault(zone) ?? [];

        public StorageVolumeInfo GetVolumeInfo() => Volume;
    }

    private sealed class FakeStore : IStorageInsightStore
    {
        public Dictionary<MediaAssetStorageState, IReadOnlySet<string>> Known { get; } = [];

        public List<HeldFileGroup> Held { get; } = [];

        public List<StorageTableInfo> Tables { get; } = [];

        public Task<IReadOnlyDictionary<MediaAssetStorageState, IReadOnlySet<string>>> GetKnownStoredNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<MediaAssetStorageState, IReadOnlySet<string>>>(Known);

        public Task<IReadOnlyList<HeldFileGroup>> GetHeldFilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HeldFileGroup>>(Held);

        public Task<IReadOnlyList<StorageTableInfo>> GetTableInfoAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StorageTableInfo>>(Tables);
    }

    private static StorageInsightService Service(FakeStore store, FakeStaging staging, bool admin = true) =>
        new(store, staging, new FakeUser(admin ? Guid.NewGuid() : null), new FakeAdmin(admin), new FixedClock(Now));

    [TestMethod]
    public async Task OnlyAnAdministratorMaySeeStorageAndNothingIsReadForAnyoneElse()
    {
        var result = await Service(new FakeStore(), new FakeStaging(), admin: false).GetAsync(CancellationToken.None);

        Assert.IsTrue(result.Forbidden);
        Assert.IsNull(result.Insight);
    }

    [TestMethod]
    public async Task FilesWithNoMatchingRecordAreUnrecognizedOnlyAfterTheGracePeriod()
    {
        var staging = new FakeStaging();
        var store = new FakeStore();
        staging.Files[MediaAssetStorageState.Quarantine] =
        [
            new("known.epub", 1_000, Now.AddDays(-3)),
            new("leftover-old.epub", 5_000, Now.AddDays(-2)),
            new("leftover-young.epub", 7_000, Now.AddHours(-3)),
            new("leftover-edge.epub", 9_000, Now - StorageInsightService.UnrecognizedGrace)
        ];
        store.Known[MediaAssetStorageState.Quarantine] = new HashSet<string> { "known.epub" };

        var insight = (await Service(store, staging).GetAsync(CancellationToken.None)).Insight!;

        var zone = insight.Zones.Single(z => z.Zone == MediaAssetStorageState.Quarantine);
        Assert.AreEqual(4, zone.Files);
        Assert.AreEqual(22_000, zone.Bytes);
        Assert.AreEqual(2, zone.UnrecognizedFiles, "The two older than a day with no record; the three-hour-old one may still be arriving.");
        Assert.AreEqual(14_000, zone.UnrecognizedBytes);
        Assert.AreEqual(Now.AddDays(-3), zone.OldestFileUtc);
    }

    [TestMethod]
    public async Task AFileKnownInADifferentZoneStillCountsAsUnrecognizedInThisOne()
    {
        var staging = new FakeStaging();
        var store = new FakeStore();
        staging.Files[MediaAssetStorageState.Rejected] = [new("moved.epub", 100, Now.AddDays(-5))];
        store.Known[MediaAssetStorageState.Trusted] = new HashSet<string> { "moved.epub" };

        var insight = (await Service(store, staging).GetAsync(CancellationToken.None)).Insight!;

        Assert.AreEqual(1, insight.Zones.Single(z => z.Zone == MediaAssetStorageState.Rejected).UnrecognizedFiles,
            "The record says it should be elsewhere, so the copy here is a stray.");
    }

    [TestMethod]
    public async Task EveryFileZoneIsListedEvenWhenEmptyAndTablesAreLargestFirstWithATotal()
    {
        var staging = new FakeStaging();
        var store = new FakeStore();
        store.Tables.AddRange([new("a", "small", 10, 1_000), new("b", "big", 500, 9_000), new("a", "mid", 50, 5_000)]);
        store.Held.Add(new HeldFileGroup(MediaAssetStorageState.Unmatched, RequestMediaType.Audiobook, false, 2, 900, Now.AddDays(-4)));

        var insight = (await Service(store, staging).GetAsync(CancellationToken.None)).Insight!;

        CollectionAssert.AreEquivalent(
            new[] { MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, MediaAssetStorageState.Trusted, MediaAssetStorageState.Unmatched, MediaAssetStorageState.Rejected },
            insight.Zones.Select(z => z.Zone).ToArray());
        Assert.IsTrue(insight.Zones.All(z => z.Files == 0 && z.OldestFileUtc is null));
        CollectionAssert.AreEqual(BigMidSmall, insight.Tables.Select(t => t.Table).ToArray());
        Assert.AreEqual(15_000, insight.DatabaseApproximateBytes);
        Assert.AreEqual(1, insight.HeldFiles.Count);
        Assert.AreEqual(50L * 1024 * 1024 * 1024, insight.Volume.FreeBytes);
    }

    [TestMethod]
    public void TheInsightCarriesNoFileNameTitleOrPathAnywhereInItsShape()
    {
        var properties = new[] { typeof(StorageInsight), typeof(StorageZoneInfo), typeof(HeldFileGroup), typeof(StorageTableInfo), typeof(StorageVolumeInfo) }
            .SelectMany(type => type.GetProperties()).Select(property => property.Name).ToArray();

        foreach (var forbidden in new[] { "Name", "Title", "Author", "Path", "Filename", "Sha", "Hash" })
            Assert.IsFalse(properties.Any(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase) && name != "UnrecognizedFiles"),
                $"The storage insight must not carry '{forbidden}'.");
    }

    [TestMethod]
    public void TheRealStagingStoreListsOnlyFilesWithTheirSizesIgnoresSubfoldersAndReportsAMissingZoneAsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "fl-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemAssetStagingStore(Options.Create(new StorageOptions { RootPath = root }));
            Assert.IsEmpty(store.ListStoredFiles(MediaAssetStorageState.Unmatched), "A zone that was never created is empty, not an error.");
            var quarantine = Path.Combine(root, "quarantine");
            Directory.CreateDirectory(Path.Combine(quarantine, "a-subfolder"));
            File.WriteAllBytes(Path.Combine(quarantine, "one.epub"), new byte[1234]);
            File.WriteAllBytes(Path.Combine(quarantine, "two.mp3"), new byte[10]);
            File.WriteAllBytes(Path.Combine(quarantine, "a-subfolder", "ignored.bin"), new byte[999]);
            File.SetLastWriteTimeUtc(Path.Combine(quarantine, "one.epub"), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            var files = store.ListStoredFiles(MediaAssetStorageState.Quarantine);

            Assert.HasCount(2, files);
            var one = files.Single(f => f.StoredFilename == "one.epub");
            Assert.AreEqual(1234, one.SizeBytes);
            Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), one.LastWriteUtc);
            var volume = store.GetVolumeInfo();
            Assert.IsTrue(volume.FreeBytes > 0 && volume.TotalBytes >= volume.FreeBytes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EveryServiceInTheComposeFileRotatesItsLogsSoAnUnattendedHostCannotBeFilledByThem()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FamilyLibrarian.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var compose = File.ReadAllText(Path.Combine(root.FullName, "compose.yaml"));

        // A named anchor with a size cap and a file count, and every top-level service pointing at it.
        Assert.IsTrue(Regex.IsMatch(compose, @"x-logging:\s*&default-logging\s+driver:\s*json-file\s+options:\s+max-size:\s*""?\d+m""?\s+max-file:\s*""?\d+""?",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), "The shared logging anchor must set a json-file size cap and file count.");
        var servicesBlock = compose[(compose.IndexOf("\nservices:", StringComparison.Ordinal) + "\nservices:".Length)..];
        // The services end where the next top-level key (such as volumes:) begins.
        var next = servicesBlock.IndexOf("\nvolumes:", StringComparison.Ordinal);
        if (next >= 0) servicesBlock = servicesBlock[..next];
        var services = Regex.Split(servicesBlock, @"(?m)^  (?=[a-z][a-z-]*:\s*$)").Skip(1).ToArray();
        Assert.IsTrue(services.Length >= 4, "postgres, migrate, clamav and family-librarian.");
        foreach (var service in services)
        {
            var name = service[..service.IndexOf(':', StringComparison.Ordinal)];
            Assert.IsTrue(service.Contains("logging: *default-logging", StringComparison.Ordinal), $"Service '{name}' must use the rotating log configuration.");
        }
    }
}
