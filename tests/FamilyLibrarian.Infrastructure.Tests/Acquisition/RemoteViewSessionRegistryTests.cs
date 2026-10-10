using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class RemoteViewSessionRegistryTests
{
    [TestMethod]
    public void AFirstAcquireForAJobSucceeds()
    {
        var registry = new RemoteViewSessionRegistry();

        var acquired = registry.TryAcquire(Guid.NewGuid(), out var closeSignal);

        Assert.IsTrue(acquired);
        Assert.IsNotNull(closeSignal);
        Assert.IsFalse(closeSignal.IsCancellationRequested);
    }

    [TestMethod]
    public void ASecondConcurrentAcquireForTheSameJobFails()
    {
        var registry = new RemoteViewSessionRegistry();
        var jobId = Guid.NewGuid();
        registry.TryAcquire(jobId, out _);

        var acquiredAgain = registry.TryAcquire(jobId, out var secondSignal);

        Assert.IsFalse(acquiredAgain);
        Assert.IsNull(secondSignal);
    }

    [TestMethod]
    public void AcquireSucceedsAgainAfterRelease()
    {
        var registry = new RemoteViewSessionRegistry();
        var jobId = Guid.NewGuid();
        registry.TryAcquire(jobId, out _);

        registry.Release(jobId);
        var acquiredAgain = registry.TryAcquire(jobId, out var secondSignal);

        Assert.IsTrue(acquiredAgain);
        Assert.IsNotNull(secondSignal);
    }

    [TestMethod]
    public void ReleasingAJobWithNoActiveSessionIsANoOp()
    {
        var registry = new RemoteViewSessionRegistry();

        registry.Release(Guid.NewGuid());
    }

    [TestMethod]
    public void RequestCloseCancelsTheHeldSignal()
    {
        var registry = new RemoteViewSessionRegistry();
        var jobId = Guid.NewGuid();
        registry.TryAcquire(jobId, out var closeSignal);

        var closed = registry.RequestClose(jobId);

        Assert.IsTrue(closed);
        Assert.IsTrue(closeSignal.IsCancellationRequested);
    }

    [TestMethod]
    public void RequestCloseForAJobWithNoActiveSessionReturnsFalse()
    {
        var registry = new RemoteViewSessionRegistry();

        var closed = registry.RequestClose(Guid.NewGuid());

        Assert.IsFalse(closed);
    }
}
