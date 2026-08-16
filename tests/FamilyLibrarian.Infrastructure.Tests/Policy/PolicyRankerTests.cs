using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Policy;
using FamilyLibrarian.Domain.Policy;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Policy;

[TestClass]
public sealed class PolicyRankerTests
{
    private readonly PolicyRanker ranker = new();

    [TestMethod]
    public void ManualChoiceNeverRecommendsEvenWithOptionsPresent()
    {
        var options = new[] { Owned(), DirectAcquisition(0m) };

        var recommendation = ranker.Recommend(options, PolicyProfileIds.ManualChoice);

        Assert.IsNull(recommendation);
    }

    [TestMethod]
    public void AnUnrecognizedProfileIdFailsClosedToNoRecommendation()
    {
        var options = new[] { Owned() };

        var recommendation = ranker.Recommend(options, "not-a-real-profile");

        Assert.IsNull(recommendation);
    }

    [TestMethod]
    public void AnEmptyOptionListReturnsNullForEveryProfile()
    {
        Assert.IsNull(ranker.Recommend([], PolicyProfileIds.LibraryFirst));
        Assert.IsNull(ranker.Recommend([], PolicyProfileIds.FreeFirst));
        Assert.IsNull(ranker.Recommend([], PolicyProfileIds.LowestCost));
        Assert.IsNull(ranker.Recommend([], PolicyProfileIds.ManualChoice));
    }

    [TestMethod]
    public void LibraryFirstPrefersOwnedOverDirectAcquisition()
    {
        var owned = Owned();
        var options = new[] { DirectAcquisition(0m), owned };

        var recommendation = ranker.Recommend(options, PolicyProfileIds.LibraryFirst);

        Assert.IsNotNull(recommendation);
        Assert.AreSame(owned, recommendation.Option);
        Assert.AreEqual("Already in your library", recommendation.Reason);
    }

    [TestMethod]
    public void FreeFirstPrefersAFreeOptionOverAPaidStoreOfferEvenWithoutAnOwnedCopy()
    {
        var free = DirectAcquisition(0m);
        var options = new[] { StoreOffer(9.99m), free };

        var recommendation = ranker.Recommend(options, PolicyProfileIds.FreeFirst);

        Assert.IsNotNull(recommendation);
        Assert.AreSame(free, recommendation.Option);
        Assert.AreEqual("Free to download", recommendation.Reason);
    }

    [TestMethod]
    public void LowestCostPicksTheCheapestOfSeveralCostedOptions()
    {
        var cheapest = StoreOffer(3.99m);
        var options = new[] { StoreOffer(9.99m), cheapest, StoreOffer(5.00m) };

        var recommendation = ranker.Recommend(options, PolicyProfileIds.LowestCost);

        Assert.IsNotNull(recommendation);
        Assert.AreSame(cheapest, recommendation.Option);
    }

    private static FulfillmentOption Owned() => new(
        ProviderId: "cwa",
        ProviderResultId: "1",
        WorkId: Guid.NewGuid(),
        EditionId: null,
        MediaType: RequestMediaType.Ebook,
        OptionKind: OptionKind.Owned,
        AcquisitionMethod: AcquisitionMethod.OwnedImport,
        Format: "epub",
        Language: null,
        Quality: null,
        Availability: null,
        Cost: null,
        Currency: null,
        LicenseOrUsageStatus: null,
        DrmStatus: null,
        ExternalActionUri: null,
        ProviderData: null);

    private static FulfillmentOption DirectAcquisition(decimal cost) => new(
        ProviderId: "gutendex",
        ProviderResultId: "1342",
        WorkId: Guid.NewGuid(),
        EditionId: null,
        MediaType: RequestMediaType.Ebook,
        OptionKind: OptionKind.DirectAcquisition,
        AcquisitionMethod: AcquisitionMethod.DirectDownload,
        Format: "epub",
        Language: null,
        Quality: null,
        Availability: null,
        Cost: cost,
        Currency: null,
        LicenseOrUsageStatus: "Public domain",
        DrmStatus: null,
        ExternalActionUri: null,
        ProviderData: "https://example.test/book.epub");

    private static FulfillmentOption StoreOffer(decimal cost) => new(
        ProviderId: "store",
        ProviderResultId: Guid.NewGuid().ToString(),
        WorkId: Guid.NewGuid(),
        EditionId: null,
        MediaType: RequestMediaType.Ebook,
        OptionKind: OptionKind.StoreOffer,
        AcquisitionMethod: AcquisitionMethod.Purchase,
        Format: "epub",
        Language: null,
        Quality: null,
        Availability: null,
        Cost: cost,
        Currency: "USD",
        LicenseOrUsageStatus: null,
        DrmStatus: null,
        ExternalActionUri: null,
        ProviderData: null);
}
