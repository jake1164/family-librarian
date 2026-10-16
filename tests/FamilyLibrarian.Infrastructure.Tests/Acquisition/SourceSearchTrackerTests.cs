using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class SourceSearchTrackerTests
{
    [TestMethod]
    public void ConcurrentSearchesStayVisibleUntilTheirOwnLeaseEnds()
    {
        var tracker = new SourceSearchTracker();
        var changes = 0;
        tracker.Changed += () => changes++;
        using var first = tracker.Begin("example-source");
        using var second = tracker.Begin("example-source");
        using var other = tracker.Begin("other-source");
        Assert.AreEqual(2, tracker.Snapshot().Count(id => id == "example-source"));
        first.Dispose();
        first.Dispose();
        Assert.AreEqual(1, tracker.Snapshot().Count(id => id == "example-source"));
        Assert.AreEqual(4, changes);
        second.Dispose();
        other.Dispose();
        Assert.AreEqual(0, tracker.Snapshot().Count);
        Assert.AreEqual(6, changes);
        Assert.AreEqual(2L, tracker.StartedCount("EXAMPLE-SOURCE"));
        Assert.AreEqual(1L, tracker.StartedCount("other-source"));
        Assert.AreEqual(0L, new SourceSearchTracker().StartedCount("example-source"));
    }

    [TestMethod]
    public async Task CancellationAndFailureReleaseTheSearch()
    {
        var tracker = new SourceSearchTracker();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var lease = tracker.Begin("example-source");
            await Task.FromCanceled(new CancellationToken(canceled: true));
        });
        Assert.AreEqual(0, tracker.Snapshot().Count);
        Assert.Throws<HttpRequestException>(() =>
        {
            using var lease = tracker.Begin("example-source");
            throw new HttpRequestException("Search failed.");
        });
        Assert.AreEqual(0, tracker.Snapshot().Count);
        Assert.AreEqual(2L, tracker.StartedCount("example-source"));
    }
}
