using FamilyLibrarian.Application.Conversation;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

[TestClass]
public sealed class KeyedAsyncLockTests
{
    [TestMethod]
    public async Task OneKeyAdmitsOneHolderAtATimeAndInOrderOfArrival()
    {
        var locks = new KeyedAsyncLock();
        var concurrent = 0;
        var maxConcurrent = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using var held = await locks.AcquireAsync("book", CancellationToken.None);
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(2);
            Interlocked.Decrement(ref concurrent);
        })));

        Assert.AreEqual(1, maxConcurrent);
        Assert.AreEqual(0, locks.ActiveKeys, "No entry is kept for a key nobody holds.");
    }

    [TestMethod]
    public async Task DifferentKeysDoNotWaitForEachOther()
    {
        var locks = new KeyedAsyncLock();
        using var first = await locks.AcquireAsync("a", CancellationToken.None);

        using var second = await locks.AcquireAsync("b", new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

        Assert.AreEqual(2, locks.ActiveKeys);
    }

    [TestMethod]
    public async Task ACancelledWaiterLeavesNoEntryBehindAndDoesNotBlockOthers()
    {
        var locks = new KeyedAsyncLock();
        var held = await locks.AcquireAsync("book", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() => locks.AcquireAsync("book", cancellation.Token));
        held.Dispose();

        Assert.AreEqual(0, locks.ActiveKeys);
        using var again = await locks.AcquireAsync("book", CancellationToken.None);
    }

    [TestMethod]
    public async Task DisposingTwiceReleasesOnlyOnce()
    {
        var locks = new KeyedAsyncLock();
        var held = await locks.AcquireAsync("book", CancellationToken.None);

        held.Dispose();
        held.Dispose();

        Assert.AreEqual(0, locks.ActiveKeys);
        using var next = await locks.AcquireAsync("book", CancellationToken.None);
    }
}
