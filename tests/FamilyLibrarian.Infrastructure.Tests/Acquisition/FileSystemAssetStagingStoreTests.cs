using System.Security.Cryptography;
using System.Text;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Acquisition;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class FileSystemAssetStagingStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AWrittenFileIsChecksummedAndDetectedCorrectly()
    {
        var store = CreateStore();
        var epubBytes = BuildMinimalEpubBytes();

        var staged = await store.WriteToQuarantineAsync(
            new MemoryStream(epubBytes), "My Book.epub", maxSizeBytes: 1_000_000, CancellationToken.None);

        Assert.AreEqual(epubBytes.Length, staged.SizeBytes);
        Assert.AreEqual("application/epub+zip", staged.DetectedMimeType);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(epubBytes)), staged.Sha256);

        var writtenPath = Path.Combine(_root, "quarantine", staged.StoredFilename);
        Assert.IsTrue(File.Exists(writtenPath));
        CollectionAssert.AreEqual(epubBytes, await File.ReadAllBytesAsync(writtenPath));
    }

    [TestMethod]
    public async Task TheStoredFilenameIsNeverDerivedFromTheOriginalName()
    {
        var store = CreateStore();

        var staged = await store.WriteToQuarantineAsync(
            new MemoryStream("content"u8.ToArray()),
            "../../etc/passwd.epub",
            maxSizeBytes: 1_000_000,
            CancellationToken.None);

        Assert.IsFalse(staged.StoredFilename.Contains("..", StringComparison.Ordinal));
        Assert.IsFalse(staged.StoredFilename.Contains("etc", StringComparison.Ordinal));
        Assert.IsFalse(staged.StoredFilename.Contains("passwd", StringComparison.Ordinal));

        var quarantineDirectory = Path.GetFullPath(Path.Combine(_root, "quarantine"));
        var writtenPath = Path.GetFullPath(Path.Combine(quarantineDirectory, staged.StoredFilename));
        Assert.IsTrue(writtenPath.StartsWith(quarantineDirectory, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AStreamExceedingTheLimitIsRejectedAndCleanedUp()
    {
        var store = CreateStore();
        var oversized = new byte[1_000];

        await Assert.ThrowsExactlyAsync<AssetTooLargeException>(() =>
            store.WriteToQuarantineAsync(
                new MemoryStream(oversized), "book.epub", maxSizeBytes: 100, CancellationToken.None));

        var quarantineDirectory = Path.Combine(_root, "quarantine");
        Assert.IsFalse(Directory.Exists(quarantineDirectory) && Directory.EnumerateFiles(quarantineDirectory).Any());
    }

    [TestMethod]
    public async Task AnUnrecognizedFileTypeIsReportedAsOctetStream()
    {
        var store = CreateStore();

        var staged = await store.WriteToQuarantineAsync(
            new MemoryStream("just plain text"u8.ToArray()),
            "notes.epub",
            maxSizeBytes: 1_000_000,
            CancellationToken.None);

        Assert.AreEqual("application/octet-stream", staged.DetectedMimeType);
    }

    [TestMethod]
    public async Task MoveAsyncRelocatesTheFileBetweenZones()
    {
        var store = CreateStore();
        var staged = await store.WriteToQuarantineAsync(
            new MemoryStream("epub bytes"u8.ToArray()), "book.epub", maxSizeBytes: 1_000_000, CancellationToken.None);

        await store.MoveAsync(
            MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, staged.StoredFilename, CancellationToken.None);

        Assert.IsFalse(File.Exists(Path.Combine(_root, "quarantine", staged.StoredFilename)));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "processing", staged.StoredFilename)));
    }

    [TestMethod]
    public async Task MoveAsyncIsANoOpWhenTheFileIsAlreadyAtItsDestination()
    {
        // Simulates a retry after a crash between a completed file move and the
        // matching database commit — see SecurityEvaluationService's remarks
        // (F3). Before this fix, a second MoveAsync call in this situation
        // threw FileNotFoundException on every retry, forever: the source had
        // nothing left to move.
        var store = CreateStore();
        var staged = await store.WriteToQuarantineAsync(
            new MemoryStream("epub bytes"u8.ToArray()), "book.epub", maxSizeBytes: 1_000_000, CancellationToken.None);
        await store.MoveAsync(
            MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, staged.StoredFilename, CancellationToken.None);

        // No exception: the file is already exactly where this call would put it.
        await store.MoveAsync(
            MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, staged.StoredFilename, CancellationToken.None);

        Assert.IsTrue(File.Exists(Path.Combine(_root, "processing", staged.StoredFilename)));
    }

    [TestMethod]
    public async Task MoveAsyncStillThrowsWhenNeitherZoneHasTheFile()
    {
        var store = CreateStore();

        // A genuinely missing file — not the "already moved" case above, since
        // the destination doesn't have it either — must still surface as an
        // error rather than being silently treated as a no-op success.
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            store.MoveAsync(
                MediaAssetStorageState.Quarantine,
                MediaAssetStorageState.Processing,
                $"{Guid.NewGuid():N}.epub",
                CancellationToken.None));
    }

    [TestMethod]
    public async Task DeleteAsyncPermanentlyRemovesAStagedFileAndCanBeRetried()
    {
        var store = CreateStore();
        var staged = await store.WriteToQuarantineAsync(
            new MemoryStream("epub bytes"u8.ToArray()), "book.epub", maxSizeBytes: 1_000_000, CancellationToken.None);

        await store.DeleteAsync(MediaAssetStorageState.Quarantine, staged.StoredFilename, CancellationToken.None);
        await store.DeleteAsync(MediaAssetStorageState.Quarantine, staged.StoredFilename, CancellationToken.None);

        Assert.IsFalse(File.Exists(Path.Combine(_root, "quarantine", staged.StoredFilename)));
    }

    private FileSystemAssetStagingStore CreateStore() =>
        new(Options.Create(new StorageOptions { RootPath = _root }));

    /// <summary>
    /// A ZIP local-file-header for an entry named "mimetype" whose stored
    /// (uncompressed) content is the EPUB content-type string — the minimum
    /// byte layout <see cref="SignatureFileTypeDetector"/> looks for.
    /// </summary>
    private static byte[] BuildMinimalEpubBytes()
    {
        const string entryName = "mimetype";
        const string content = "application/epub+zip";
        var nameBytes = Encoding.ASCII.GetBytes(entryName);
        var contentBytes = Encoding.ASCII.GetBytes(content);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0x04034B50u); // local file header signature
        writer.Write((ushort)20);  // version needed
        writer.Write((ushort)0);   // flags
        writer.Write((ushort)0);   // compression method: stored
        writer.Write((ushort)0);   // mod time
        writer.Write((ushort)0);   // mod date
        writer.Write(0u);          // crc32 (unchecked by the detector)
        writer.Write((uint)contentBytes.Length); // compressed size
        writer.Write((uint)contentBytes.Length); // uncompressed size
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);   // extra field length
        writer.Write(nameBytes);
        writer.Write(contentBytes);

        return stream.ToArray();
    }
}
