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
}
