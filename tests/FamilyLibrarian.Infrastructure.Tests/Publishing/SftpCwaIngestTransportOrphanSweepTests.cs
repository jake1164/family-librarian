using FamilyLibrarian.Infrastructure.Publishing;
using Renci.SshNet.Sftp;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

/// <summary>
/// Covers <c>SftpCwaIngestTransport.DeleteOrphanedUploads</c> -- the pure
/// decision logic behind the orphan sweep, exercised through the internal
/// <see cref="ISftpDirectoryClient"/> seam since the real SFTP client and its
/// directory-entry type are not something a unit test can construct.
/// </summary>
[TestClass]
public sealed class SftpCwaIngestTransportOrphanSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AStaleUploadingTempFileIsDeleted()
    {
        var staleFile = new FakeSftpFile(
            name: $".{Guid.NewGuid():N}.uploading",
            fullName: "/ingest/.stale.uploading",
            isRegularFile: true,
            lastWriteTimeUtc: Now.UtcDateTime - TimeSpan.FromMinutes(16));
        var client = new FakeSftpDirectoryClient([staleFile]);

        SftpCwaIngestTransport.DeleteOrphanedUploads(client, "/ingest", Now);

        CollectionAssert.AreEqual(new[] { staleFile.FullName }, client.DeletedPaths);
    }

    [TestMethod]
    public void AFreshUploadingTempFileIsLeftAlone()
    {
        var freshFile = new FakeSftpFile(
            name: $".{Guid.NewGuid():N}.uploading",
            fullName: "/ingest/.fresh.uploading",
            isRegularFile: true,
            lastWriteTimeUtc: Now.UtcDateTime - TimeSpan.FromMinutes(14));
        var client = new FakeSftpDirectoryClient([freshFile]);

        SftpCwaIngestTransport.DeleteOrphanedUploads(client, "/ingest", Now);

        Assert.AreEqual(0, client.DeletedPaths.Count);
    }

    [TestMethod]
    public void AFileNotMatchingTheTempFilePatternIsLeftAloneRegardlessOfAge()
    {
        var unrelatedFile = new FakeSftpFile(
            name: "the-hobbit.epub",
            fullName: "/ingest/the-hobbit.epub",
            isRegularFile: true,
            lastWriteTimeUtc: Now.UtcDateTime - TimeSpan.FromDays(1));
        var client = new FakeSftpDirectoryClient([unrelatedFile]);

        SftpCwaIngestTransport.DeleteOrphanedUploads(client, "/ingest", Now);

        Assert.AreEqual(0, client.DeletedPaths.Count);
    }

    [TestMethod]
    public void ADirectoryEntryIsNeverTreatedAsAnOrphanedUpload()
    {
        var directoryEntry = new FakeSftpFile(
            name: $".{Guid.NewGuid():N}.uploading",
            fullName: "/ingest/.stale.uploading",
            isRegularFile: false,
            lastWriteTimeUtc: Now.UtcDateTime - TimeSpan.FromMinutes(30));
        var client = new FakeSftpDirectoryClient([directoryEntry]);

        SftpCwaIngestTransport.DeleteOrphanedUploads(client, "/ingest", Now);

        Assert.AreEqual(0, client.DeletedPaths.Count);
    }

    private sealed class FakeSftpDirectoryClient(IReadOnlyList<ISftpFile> entries) : ISftpDirectoryClient
    {
        public List<string> DeletedPaths { get; } = [];

        public IEnumerable<ISftpFile> ListDirectory(string path) => entries;

        public void DeleteFile(string path) => DeletedPaths.Add(path);
    }

    /// <summary>
    /// A minimal <see cref="ISftpFile"/> stand-in: SSH.NET's own
    /// implementation (<c>Renci.SshNet.Sftp.SftpFile</c>) has no public
    /// constructor, so a fake has to implement the interface directly. Only
    /// the members the sweep actually reads (<see cref="Name"/>,
    /// <see cref="IsRegularFile"/>, <see cref="LastWriteTimeUtc"/>,
    /// <see cref="FullName"/>) are backed by real values -- everything else
    /// the interface requires but the sweep never touches throws, so a test
    /// would fail loudly if that ever changed.
    /// </summary>
    private sealed class FakeSftpFile(
        string name,
        string fullName,
        bool isRegularFile,
        DateTime lastWriteTimeUtc) : ISftpFile
    {
        public string Name { get; } = name;
        public string FullName { get; } = fullName;
        public bool IsRegularFile { get; } = isRegularFile;
        public DateTime LastWriteTimeUtc { get; set; } = lastWriteTimeUtc;

        public SftpFileAttributes Attributes => throw new NotSupportedException();
        public DateTime LastAccessTime
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public DateTime LastWriteTime
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public DateTime LastAccessTimeUtc
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public long Length => throw new NotSupportedException();
        public int UserId
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public int GroupId
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool IsSocket => throw new NotSupportedException();
        public bool IsSymbolicLink => throw new NotSupportedException();
        public bool IsBlockDevice => throw new NotSupportedException();
        public bool IsDirectory => throw new NotSupportedException();
        public bool IsCharacterDevice => throw new NotSupportedException();
        public bool IsNamedPipe => throw new NotSupportedException();
        public bool OwnerCanRead
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool OwnerCanWrite
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool OwnerCanExecute
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool GroupCanRead
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool GroupCanWrite
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool GroupCanExecute
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool OthersCanRead
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool OthersCanWrite
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public bool OthersCanExecute
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public void SetPermissions(short mode) => throw new NotSupportedException();
        public void Delete() => throw new NotSupportedException();
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void MoveTo(string destFileName) => throw new NotSupportedException();
        public void UpdateStatus() => throw new NotSupportedException();
    }
}
