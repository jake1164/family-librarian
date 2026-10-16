using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

[TestClass]
public sealed class CollectionContainmentTests
{
    [TestMethod]
    [DataRow("Example Series Books 1-3", null)]
    [DataRow("First Novel / Second Novel", "Example Author - Second Novel and more")]
    public void KnownContainmentAllowsOnlyACollectionQuestion(string title, string? releaseName)
    {
        var requested = new BookIdentity("Second Novel", "Example Author", [], Series: [new("Example Series", "2")]);
        var candidate = ExternalProviderCandidate.FromSimple("collection", title, "Example Author", "epub", 123456);
        candidate = candidate with { Release = candidate.Release! with { IsCollection = true, Name = releaseName } };
        var assessment = DeterministicCandidateIdentityResolver.Assess(requested, candidate);
        var option = new FulfillmentOption("example-source", "collection", Guid.Empty, null, RequestMediaType.Ebook, OptionKind.DirectAcquisition,
            AcquisitionMethod.DirectDownload, "epub", null, null, null, null, null, null, null, null, null,
            RequiresReleaseConfirmation: true, Title: title, IdentityAssessment: assessment);
        Assert.AreEqual(AcquisitionQuestionKind.CollectionContaining, AcquisitionQuestionPolicy.KindOf(requested, option, ProviderConfidenceLevel.Strict),
            $"Conditions: {string.Join("; ", assessment.Conditions.Select(c => c.Code + ": " + c.Reason))}; author={assessment.AuthorEvidence.Kind}; contradictions={string.Join("; ", assessment.Contradictions)}");
    }

    [TestMethod]
    [DataRow("The Empyrean Books 1-3", "Contains")]
    [DataRow("The Empyrean Books 4-6", "DoesNotContain")]
    [DataRow("The Empyrean #1-3", "Contains")]
    [DataRow("The Empyrean Vol. 1-3", "Contains")]
    [DataRow("The Empyrean 1-3", "Contains")]
    [DataRow("Other Series Books 1-3", "Unknown")]
    [DataRow("Fourth Wing / Iron Flame / Onyx Storm", "Contains")]
    [DataRow("Fourth Wing & Iron Flame", "Contains")]
    [DataRow("Romantasy Box Set", "Unknown")]
    [DataRow("The Empyrean Books 3-1", "Unknown")]
    public void ContainmentRequiresAnExactListingOrAnInclusiveRangeOfTheRequestedSeries(string title, string verdict)
    {
        var requested = new BookIdentity("Iron Flame", "Rebecca Yarros", [], Series: [new("The Empyrean", "2")]);
        var candidate = ExternalProviderCandidate.FromSimple("collection", title, "Rebecca Yarros", "epub", null);
        // Reflection lets the acceptance fixture fail on the unchanged implementation, before its new API exists.
        var type = typeof(BookIdentity).Assembly.GetType("FamilyLibrarian.Application.Matching.CollectionContainment");
        Assert.IsNotNull(type, "The containment policy must exist.");
        var result = type.GetMethod("Evaluate")!.Invoke(null, [requested, candidate]);
        Assert.AreEqual(verdict, result?.ToString());
    }
}
