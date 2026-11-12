using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ExternalProviderSameWorkCollapseEndpointTests
{
    private static readonly BookIdentity Requested = new("Fourth Wing", "Rebecca Yarros", [], Series: [new("The Empyrean", "1")]);

    private static ExternalProviderCandidate[] Editions() =>
    [
        ExternalProviderCandidate.FromSimple("edition-c", "Fourth Wing: Special Edition", "Rebecca Yarros", "epub", 1_000_000),
        ExternalProviderCandidate.FromSimple("edition-a", "Fourth Wing (Empyrean 1)", "Rebecca Yarros", "epub", 1_200_000),
        ExternalProviderCandidate.FromSimple("edition-b", "Fourth Wing [Illustrated Edition]", "Rebecca Yarros", "epub", 1_400_000)
    ];

    [TestMethod]
    public async Task CharacterizedSubtitleEditionsHaveNoIndividualBasisInAMultiCandidatePool()
    {
        var candidates = Editions();
        var verifier = new ExternalProviderMatchVerifier(new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()), new DeterministicBookMatcher());
        var verdicts = await verifier.VerifyAsync(Requested, candidates, CancellationToken.None);
        foreach (var candidate in candidates)
        {
            var verdict = verdicts[candidate.ProviderReference];
            Assert.AreEqual(WorkIdentityDecision.Ambiguous, verdict.IdentityAssessment!.Decision, candidate.Title);
            Assert.IsNull(verdict.Basis, candidate.Title);
            Assert.AreEqual(AuthorAffinityKind.Exact, verdict.AuthorAffinity!.Kind, candidate.Title);
            Assert.HasCount(0, verdict.IdentityAssessment.Conditions, candidate.Title);
            Assert.AreEqual(Requested.Title, WorkTitleCore.Reduce(candidate.Title), candidate.Title);
        }
    }

    [TestMethod]
    [DataRow("Strict", false)]
    [DataRow("Standard", true)]
    [DataRow("Liberal", true)]
    public async Task SameWorkSubtitlePoolDownloadsOnlyAtStandardOrLiberal(string level, bool fetched)
    {
        var status = RequestStatus.PendingAcquisition;
        var reviewCount = -1;
        var submitted = await ConfidenceScenario.RunAsync(Editions(), Requested.Title, "1", level,
            inspect: async database =>
            {
                status = (await database.BookRequests.SingleAsync()).Status;
                reviewCount = await database.RequestReviewCandidates.CountAsync();
                var jobs = await database.ProviderAcquisitionJobs.ToArrayAsync();
                Assert.IsFalse(jobs.Any(job => job.IdentityPreConfirmed), "Pool confidence must not manufacture pre-confirmed file identity.");
            });
        Assert.AreEqual(fetched ? 1 : 0, submitted.Count, $"{level}: persisted {status}, review candidates {reviewCount}.");
        if (fetched)
        {
            Assert.AreEqual("edition-a", submitted.Single(), "The shared ranker must select a stable winner, not response order.");
            Assert.AreEqual(0, reviewCount);
        }
        else
        {
            Assert.AreEqual(RequestStatus.NeedsReview, status);
            Assert.AreEqual(3, reviewCount);
        }
    }

    [TestMethod]
    [DataRow("Strict")]
    [DataRow("Standard")]
    [DataRow("Liberal")]
    public async Task ADerivativeConditionVetoesTheWholeCollapse(string level)
    {
        var derivative = ExternalProviderCandidate.FromSimple("study-guide", "Fourth Wing [Study Guide]", "Rebecca Yarros", "epub", 1_600_000);
        var assessment = DeterministicCandidateIdentityResolver.Assess(Requested, derivative);
        Assert.IsTrue(assessment.Conditions.Count > 0);
        var submitted = await ConfidenceScenario.RunAsync([.. Editions(), derivative], Requested.Title, "1", level,
            inspect: async database => Assert.AreEqual(RequestStatus.NeedsReview, (await database.BookRequests.SingleAsync()).Status));
        Assert.IsEmpty(submitted);
    }

    [TestMethod]
    [DataRow("Strict")]
    [DataRow("Standard")]
    [DataRow("Liberal")]
    public async Task AnExactTitleAuthorWinnerStillDownloadsAtEveryLevel(string level)
    {
        var exact = ExternalProviderCandidate.FromSimple("exact", Requested.Title, "Rebecca Yarros", "epub", 900_000);
        var submitted = await ConfidenceScenario.RunAsync([.. Editions(), exact], Requested.Title, "1", level);
        Assert.HasCount(1, submitted);
        Assert.AreEqual("exact", submitted.Single());
    }

    [TestMethod]
    public async Task ADifferentWorkCannotDisappearBehindPresentationCollapse()
    {
        var editions = Editions();
        var other = editions[0] with { ProviderReference = "zz-companion", Work = editions[0].Work with { Title = "Fourth Wing Companion" } };
        var submitted = await ConfidenceScenario.RunAsync([.. editions, other], Requested.Title, "1", "Standard",
            inspect: async database => Assert.AreEqual(RequestStatus.NeedsReview, (await database.BookRequests.SingleAsync()).Status));
        Assert.IsEmpty(submitted);
    }

    [TestMethod]
    public async Task APoolWithNoSupportingAuthorCannotCollapseEvenAtLiberal()
    {
        var candidates = Editions().Select(candidate => candidate with { Work = candidate.Work with { Authors = [] } }).ToArray();
        var submitted = await ConfidenceScenario.RunAsync(candidates, Requested.Title, "1", "Liberal",
            inspect: async database => Assert.AreEqual(RequestStatus.NeedsReview, (await database.BookRequests.SingleAsync()).Status));
        Assert.IsEmpty(submitted);
    }

    [TestMethod]
    [DataRow("Strict", false)]
    [DataRow("Standard", false)]
    [DataRow("Liberal", true)]
    public async Task ASinglePlausibleTitleAuthorCandidateHonorsItsTierAtReDerivation(string level, bool fetched)
    {
        var candidate = Editions()[0];
        var verifier = new ExternalProviderMatchVerifier(new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()), new DeterministicBookMatcher());
        var verdict = (await verifier.VerifyAsync(Requested, [candidate], CancellationToken.None))[candidate.ProviderReference];
        Assert.AreEqual(BookMatchBasis.TitleAuthor, verdict.Basis);
        Assert.IsTrue(verdict.HasPlausibleTitle);
        var submitted = await ConfidenceScenario.RunAsync([candidate], Requested.Title, "1", level);
        Assert.AreEqual(fetched ? 1 : 0, submitted.Count);
    }

    [TestMethod]
    [DataRow("condition")]
    [DataRow("author")]
    public async Task AcquisitionReprovesThePoolAndRefusesChangedEvidence(string changed)
    {
        var editions = Editions();
        var plannedSearches = SearchVariantPlanner.Plan(Requested, RequestMediaType.Ebook).Count;
        var sawFreshEvidence = false;
        var submitted = await ConfidenceScenario.RunAsync(editions, Requested.Title, "1", "Standard",
            inspect: async database =>
            {
                Assert.AreEqual(ProviderAttemptOutcome.Retrying, (await database.ProviderAttempts.SingleAsync()).Outcome);
                Assert.AreEqual(0, await database.ProviderAcquisitionJobs.CountAsync());
            },
            searchScript: (count, _) =>
            {
                if (count <= plannedSearches) return editions;
                sawFreshEvidence = true;
                return editions.Select(candidate => candidate.ProviderReference != "edition-a" ? candidate : candidate with
                {
                    Work = changed == "condition" ? candidate.Work with { Title = "Fourth Wing [Study Guide]" }
                        : candidate.Work with { Authors = [new("David Yarros", "author")] }
                }).ToArray();
            });
        Assert.IsTrue(sawFreshEvidence, "The test must reach the acquisition search after pooling all variants.");
        Assert.IsEmpty(submitted);
    }

    [TestMethod]
    public async Task SameWorkEvidenceFoundAcrossSearchVariantsSurvivesReDerivation()
    {
        var editions = Editions();
        var submitted = await ConfidenceScenario.RunAsync(editions, Requested.Title, "1", "Standard",
            searchScript: (_, request) =>
                [request.Work.Title != Requested.Title ? editions[1] :
                    request.Work.Authors[0].Name != Requested.Author ? editions[2] : editions[0]]);
        Assert.HasCount(1, submitted);
        Assert.AreEqual("edition-a", submitted.Single());
    }

    [TestMethod]
    public async Task AcquisitionPreservesBaseFirstEvidenceWhenRebuildingThePool()
    {
        var editions = Editions();
        var plannedSearches = SearchVariantPlanner.Plan(Requested, RequestMediaType.Ebook).Count;
        var sawChangedBase = false;
        var submitted = await ConfidenceScenario.RunAsync(editions, Requested.Title, "1", "Standard",
            searchScript: (count, request) =>
            {
                if (count > plannedSearches && request.Work.Title == Requested.Title && request.Work.Authors[0].Name == Requested.Author)
                {
                    sawChangedBase = true;
                    return [editions[1] with { Work = editions[1].Work with { Authors = [new("David Yarros", "author")] } }];
                }
                return [request.Work.Title != Requested.Title ? editions[1] :
                    request.Work.Authors[0].Name != Requested.Author ? editions[2] : editions[0]];
            });
        Assert.IsTrue(sawChangedBase);
        Assert.IsEmpty(submitted, "The first record evidence must be the base search's, even when the selected id came from another variant.");
    }
}
