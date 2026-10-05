using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ExternalAudiobookPartEvidenceTests
{
    private static readonly string[] OrderedPartReferences = ["one", "two"];
    private static readonly int[] MissingFirstPart = [1];
    [TestMethod]
    [DataRow("Onyx.Storm.2.of.2", 2, 2)]
    [DataRow("Onyx Storm Part 1 of 2", 1, 2)]
    [DataRow("Onyx Storm PART 2-Extended", 2, 0)]
    [DataRow("Onyx Storm Disc 1 of 3", 1, 3)]
    [DataRow("Onyx Storm Part 2/2", 2, 2)]
    public void PartIdentityIsIndependentOfNamePackaging(string name, int number, int total)
    {
        var part = ExternalAudiobookPartEvidence.Parse(name);
        Assert.IsNotNull(part);
        Assert.AreEqual(number, part.Number);
        Assert.AreEqual(total == 0 ? null : (int?)total, part.Total);
        var policy = ExternalReleasePolicy.Evaluate(null, RequestMediaType.Audiobook, part);
        Assert.IsTrue(policy.RequiresConfirmation);
        StringAssert.Contains(policy.Reason!, part.Description);
        Assert.IsFalse(policy.Reason!.Contains("collection", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("Onyx Storm Book 3")]
    [DataRow("Onyx Storm 2")]
    [DataRow("Onyx Storm 3 of 2")]
    [DataRow("Onyx Storm 0 of 2")]
    [DataRow("Onyx Storm Part 1 of 2 Part 2 of 2")]
    public void UncertainNumberingDoesNotBecomePartEvidence(string name) =>
        Assert.IsNull(ExternalAudiobookPartEvidence.Parse(name));

    [TestMethod]
    public void APartSeparatorIsNotMistakenForACombinedWorkSeparator()
    {
        var verdict = ExternalReleaseNameEvidence.Evaluate("Onyx Storm Part 2/2", ["Onyx Storm"], "Rebecca Yarros");
        Assert.IsTrue(verdict.IsStrictWorkAssertion);
        Assert.AreEqual(2, verdict.Part!.Total);
        Assert.IsFalse(ExternalReleaseNameEvidence.Evaluate("Onyx Storm / Another Work Part 2/2",
            ["Onyx Storm"], "Rebecca Yarros").IsStrictWorkAssertion);
        Assert.IsTrue(ExternalReleasePolicy.Evaluate(new ExternalProviderReleaseEvidence(
            "Onyx Storm Part 2/2", "epub", null, false, 1, false, null, null, [], null,
            ExternalProviderDrmStatus.None), RequestMediaType.Ebook, verdict.Part).RequiresConfirmation);
    }

    [TestMethod]
    public void DifferentlyNamedPartsCanFormAnAvailableSetWithoutMatchingAuthors()
    {
        var first = Option("one", "The.Empyrean.[03].Onyx.Storm.1.of.2.by.Rebecca.Yarros.fant");
        var second = Option("two", "The.Empyrean.[03].Onyx.Storm.2.of.2");
        var set = ExternalAudiobookPartSetAssessment.For(second, [second, first]);
        Assert.IsTrue(set.HasEveryNumber);
        CollectionAssert.AreEqual(OrderedPartReferences, set.Parts.Select(part => part.ProviderResultId).ToArray());
        Assert.IsFalse(ExternalAudiobookPartSetAssessment.For(second, [second]).HasEveryNumber);
        CollectionAssert.AreEqual(MissingFirstPart, ExternalAudiobookPartSetAssessment.For(second, [second]).MissingParts.ToArray());
    }

    [TestMethod]
    public void ConflictsDifferentEditionsAndDuplicateNumbersCannotEstablishACompleteSet()
    {
        var second = Option("two", "Onyx Storm 2 of 2");
        var first = Option("one", "Onyx Storm 1 of 2");
        var conflict = first with { AuthorAffinity = AuthorAffinity.Evaluate("Rebecca Yarros", "Stephen King") };
        var variants = new[]
        {
            first with { ReleaseName = "Onyx Storm 1 of 2 [GraphicAudio]" },
            first with { Language = "fr" },
            first with { Format = "mp3" },
            first with { HasPlausibleTitle = false },
            conflict
        };
        foreach (var variant in variants)
            Assert.IsFalse(ExternalAudiobookPartSetAssessment.For(second with { Language = "en" }, [variant, second]).HasEveryNumber);
        var duplicate = first with { ProviderResultId = "other-one" };
        var set = ExternalAudiobookPartSetAssessment.For(second, [first, duplicate, second]);
        Assert.IsTrue(set.HasAmbiguousParts);
        Assert.IsFalse(set.HasEveryNumber);
        Assert.IsFalse(ExternalAudiobookPartSetAssessment.For(Option("unknown", "Onyx Storm Part 2"), [first, second]).HasEveryNumber);
    }

    private static FulfillmentOption Option(string reference, string name) => new(
        "example-source", reference, Guid.Empty, null, RequestMediaType.Audiobook, OptionKind.DirectAcquisition,
        AcquisitionMethod.DirectDownload, "m4b", null, null, null, 0m, null, null, null, null, null,
        ReleaseName: name, HasPlausibleTitle: true, AudiobookPart: ExternalAudiobookPartEvidence.Parse(name));

    [TestMethod]
    public async Task DifferentlyNamedScreenshotPartsBothAssertTheWorkAndRetainTheirNumbers()
    {
        var names = new[] { "The.Empyrean.[03].Onyx.Storm.2.of.2", "The.Empyrean.[03].Onyx.Storm.1.of.2.by.Rebecca.Yarros.fant" };
        var candidates = names.Select((name, index) => new ExternalProviderCandidate($"part-{index}",
            ExternalProviderWorkEvidence.Empty, Release: new ExternalProviderReleaseEvidence(
                name, "m4b", null, true, 1, false, null, null, [], null))).ToArray();
        var verifier = new ExternalProviderMatchVerifier(new BookMatchService(new DeterministicBookMatcher(),
            new NoOpAmbiguityResolver()), new DeterministicBookMatcher());
        var identity = new BookIdentity("Onyx Storm", "Rebecca Yarros", [], Series: [new BookSeries("Empyrean", "3")]);
        var verdicts = await verifier.VerifyAsync(identity, candidates, CancellationToken.None);
        Assert.IsTrue(verdicts.Values.All(verdict => verdict.HasPlausibleTitle));
        Assert.AreEqual(2, verdicts["part-0"].AudiobookPart!.Number);
        Assert.AreEqual(1, verdicts["part-1"].AudiobookPart!.Number);
        Assert.AreEqual(BookMatchBasis.StrictTitle, verdicts["part-0"].Basis);
        foreach (var candidate in candidates)
            Assert.IsTrue(ExternalReleasePolicy.Evaluate(candidate.Release, RequestMediaType.Audiobook,
                verdicts[candidate.ProviderReference].AudiobookPart).RequiresConfirmation);
    }
}
