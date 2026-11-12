using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ExternalProviderPartSetConfidenceEndpointTests
{
    [TestMethod]
    [DataRow("Strict", true, false, false, false)]
    [DataRow("Strict", false, false, false, false)]
    [DataRow("Standard", true, false, false, true)]
    [DataRow("Strict", true, true, false, true)]
    [DataRow("Strict", true, false, true, false)]
    public async Task EveryPartMustMeetTheEffectiveConfidence(string level, bool structuredAuthor, bool corroborated, bool mixed, bool fetched)
    {
        ExternalProviderCandidate Part(int number) => new($"part-{number}",
            structuredAuthor ? new("Onyx Storm", null, [new(mixed && number == 1 ? "Rebecca Yarros" : "Rebecca Yaros", "author")],
                corroborated ? [new("The Empyrean", "3")] : [], []) : ExternalProviderWorkEvidence.Empty,
            Release: new($"Onyx Storm ({number} of 2){(structuredAuthor ? "" : " by Rebecca Yaros")}", "m4b", 123456,
                null, null, null, null, null, [], null, ExternalProviderDrmStatus.None));
        var submitted = await ConfidenceScenario.RunAsync([Part(1), Part(2)], "Onyx Storm", "3", level, "Audiobook",
            inspect: async database =>
            {
                var request = await database.BookRequests.SingleAsync();
                var jobs = await database.ProviderAcquisitionJobs.CountAsync();
                Assert.AreEqual(fetched ? RequestStatus.PendingAcquisition : RequestStatus.NeedsReview, request.Status,
                    $"{level}, structured={structuredAuthor}, corroborated={corroborated}, mixed={mixed}, jobs={jobs}");
                if (!fetched)
                {
                    Assert.AreEqual(RequestReviewCategory.PreferenceAmbiguity, request.ReviewCategory);
                    Assert.AreEqual(2, await database.RequestReviewCandidates.CountAsync());
                }
            });
        Assert.AreEqual(fetched ? 2 : 0, submitted.Count);
    }

    [TestMethod]
    [DataRow("Strict")]
    [DataRow("Standard")]
    [DataRow("Liberal")]
    public async Task AllStrongPartsRetainAutomaticSelection(string level)
    {
        ExternalProviderCandidate Part(int number) => new($"part-{number}",
            new("Onyx Storm", null, [new("Rebecca Yarros", "author")], [], []),
            Release: new($"Onyx Storm ({number} of 2)", "m4b", 123456, null, null, null, null, null, [], null, ExternalProviderDrmStatus.None));
        var submitted = await ConfidenceScenario.RunAsync([Part(1), Part(2)], "Onyx Storm", "", level, "Audiobook");
        Assert.AreEqual(2, submitted.Count);
    }
}
