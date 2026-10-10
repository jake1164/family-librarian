using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Catalog;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class CatalogSearchStateTests
{
    [TestMethod]
    public void BeginNewSearchClearsPriorAvailabilityEnrichment()
    {
        var state = new CatalogSearchState();
        state.AvailabilityByResultKey["metadata:sum-of-all-fears"] =
            new CandidateAvailabilityRunResponse(IsComplete: true, Availability: []);

        state.BeginNewSearch();

        Assert.AreEqual(0, state.AvailabilityByResultKey.Count);
    }
    [TestMethod]
    public void OpeningDetailsRetainsCompatibleGroupedEditionsButNeverMixesDifferentVersionKinds()
    {
        var fresh = new CatalogBookCandidateResponse("source", "Source", "id", "Moby Dick", ["Herman Melville"], null, null, null,
            [new("Moby Dick", "9780006546061", "Paperback", null)], [], null, null, [], null, VersionKind: "Novel");
        var grouped = fresh with { Editions = [new("Moby Dick", "9781451673319", "Hardcover", null)],
            Sources = [new("other", "Other", "other-id", null)] };
        var combined = CatalogSearchState.PreserveGroupedEditions(fresh, grouped);
        Assert.HasCount(2, combined.Editions);
        Assert.HasCount(1, combined.Sources);
        var different = CatalogSearchState.PreserveGroupedEditions(fresh with { VersionKind = "GraphicAdaptation" }, grouped);
        Assert.HasCount(1, different.Editions);
        Assert.HasCount(0, different.Sources);
    }
}
