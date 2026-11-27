using FamilyLibrarian.Infrastructure.Gutenberg;

namespace FamilyLibrarian.Infrastructure.Tests.Gutenberg;

[TestClass]
public sealed class GutenbergCatalogDownloadTests
{
    [TestMethod]
    public async Task CopiesBytesAndReportsProgressBeforeTheDownloadCompletes()
    {
        var bytes = new byte[200_000];
        new Random(1).NextBytes(bytes);
        await using var source = new MemoryStream(bytes);
        await using var destination = new MemoryStream();
        var reports = new List<long>();
        await GutenbergCatalogDownload.CopyAsync(source, destination, new SteppedClock(),
            (count, _) => { reports.Add(count); return Task.CompletedTask; }, CancellationToken.None);
        CollectionAssert.AreEqual(bytes, destination.ToArray());
        Assert.IsTrue(reports.Any(count => count > 0 && count < bytes.Length));
        Assert.AreEqual((long)bytes.Length, reports[^1]);
    }

    [TestMethod]
    public async Task ABodyThatStopsSendingBytesFailsWithAnActionableError()
    {
        await using var source = new StalledStream();
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => GutenbergCatalogDownload.CopyAsync(
            source, Stream.Null, TimeProvider.System, null, CancellationToken.None, TimeSpan.FromMilliseconds(30)));
        StringAssert.Contains(error.Message, "stopped receiving data");
    }

    [TestMethod]
    public async Task CallerCancellationIsPreservedRatherThanReportedAsAStalledSource()
    {
        await using var source = new StalledStream();
        using var cancellation = new CancellationTokenSource();
        var copying = GutenbergCatalogDownload.CopyAsync(source, Stream.Null, TimeProvider.System, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => copying);
    }

    private sealed class SteppedClock : TimeProvider
    {
        private long _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _seconds));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
