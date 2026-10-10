using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Web.Catalog;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class CatalogSearchContinuationTests
{
    [TestMethod]
    public void LaterPageIsRegroupedWithPriorCandidatesAndItsBestMatchMovesFirst()
    {
        var coordinator = new CatalogSearchRunCoordinator();
        var user = Guid.NewGuid();
        var first = coordinator.Start(user, new("Fahrenheit 451"))!;
        var guide = new BookCandidate("source", "Source", "guide", "Fahrenheit 451", ["Samuel J. Umland"], null, null, null, [], [], Publisher: "Cliffs Notes");
        first.Add("source", "Source", true, [guide], true);
        first.Complete();
        var second = coordinator.Start(user, new("Fahrenheit 451", 2), first.Id)!;
        var novel = guide with { ExternalId = "novel", Authors = ["Ray Bradbury"], Publisher = null, Description = "A dystopian novel." };
        second.Add("source", "Source", true, [novel], false);
        var results = BookCandidateGrouper.GroupMatchingCandidates(second.Snapshot().SelectMany(provider => provider.Candidates), second.Query.Text);
        Assert.HasCount(2, results);
        Assert.AreEqual("novel", results[0].ExternalId);
        Assert.IsFalse(second.Snapshot().Single().HasMore);
        Assert.HasCount(1, first.Snapshot().Single().Candidates);
    }

    [TestMethod]
    public void ContinuationCannotReuseAnotherUsersRunOrChangeQueryOrSkipPage()
    {
        var coordinator = new CatalogSearchRunCoordinator();
        var user = Guid.NewGuid();
        var first = coordinator.Start(user, new("Moby Dick"))!;
        Assert.IsNull(coordinator.Start(user, new("Moby Dick", 2), first.Id));
        first.Complete();
        Assert.IsNull(coordinator.Start(Guid.NewGuid(), new("Moby Dick", 2), first.Id));
        Assert.IsNull(coordinator.Start(user, new("Fahrenheit 451", 2), first.Id));
        Assert.IsNull(coordinator.Start(user, new("Moby Dick", 3), first.Id));
        Assert.IsNotNull(coordinator.Start(user, new("Moby Dick", 2), first.Id));
    }

    [TestMethod]
    public void FailureOnLaterPagePreservesEarlierProviderCandidates()
    {
        var run = new CatalogSearchRun(Guid.NewGuid(), new("Moby Dick", 2));
        var candidate = new BookCandidate("source", "Source", "novel", "Moby Dick", ["Herman Melville"], null, null, null, [], []);
        run.Add("source", "Source", true, [candidate], true);
        run.Add("source", "Source", false, [], false);
        Assert.HasCount(1, run.Snapshot().Single().Candidates);
        Assert.IsFalse(run.Snapshot().Single().Succeeded);
    }
}
