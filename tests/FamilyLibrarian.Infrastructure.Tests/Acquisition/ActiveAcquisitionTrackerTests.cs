using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ActiveAcquisitionTrackerTests
{
    [TestMethod]
    public void ActivityChangesStageAndClearsWhenWorkEnds()
    {
        var tracker = new ActiveAcquisitionTracker();
        var requestId = Guid.NewGuid();
        var formatId = Guid.NewGuid();

        using (var activity = tracker.Begin(requestId, formatId, "example-source", "Checking provider"))
        {
            Assert.AreEqual("Checking provider", tracker.Snapshot().Single().Stage);
            activity.SetStage("Downloading");
            Assert.AreEqual("Downloading", tracker.Snapshot().Single().Stage);
        }

        Assert.AreEqual(0, tracker.Snapshot().Count);
    }

    [TestMethod]
    public void SupersededLeaseCannotClearNewActivity()
    {
        var tracker = new ActiveAcquisitionTracker();
        var formatId = Guid.NewGuid();
        using var first = tracker.Begin(Guid.NewGuid(), formatId, "example-source", "Downloading");
        using var second = tracker.Begin(Guid.NewGuid(), formatId, "example-source", "Processing files");

        first.Dispose();
        Assert.AreEqual("Processing files", tracker.Snapshot().Single().Stage);
    }
}
