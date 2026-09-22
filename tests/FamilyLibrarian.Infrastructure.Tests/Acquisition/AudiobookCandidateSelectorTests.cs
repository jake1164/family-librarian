using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class AudiobookCandidateSelectorTests
{
    [TestMethod]
    public void HumanNarrationWinsDespiteMuchLowerPopularityUnderPreferHuman()
    {
        var human = Option("a", narration: NarrationKind.Human, downloads: 100);
        var synthetic = Option("b", narration: NarrationKind.Synthetic, downloads: 50_000);

        var result = AudiobookCandidateSelector.Select([human, synthetic], AudiobookNarrationPreference.PreferHuman);

        Assert.AreEqual(human, result.Winner);
    }

    [TestMethod]
    public void AnObscureSoleCandidateCanAutoAcquireWithNoMinimumDownloadCount()
    {
        var candidate = Option("a", downloads: 17);

        var result = AudiobookCandidateSelector.Select([candidate], AudiobookNarrationPreference.PreferHuman);

        Assert.IsTrue(result.CanAutoAcquire);
        Assert.AreEqual(candidate, result.Winner);
    }

    [TestMethod]
    public void PreferHumanFallsBackToSyntheticWhenNoHumanCandidateExists()
    {
        var synthetic = Option("a", narration: NarrationKind.Synthetic);

        var result = AudiobookCandidateSelector.Select([synthetic], AudiobookNarrationPreference.PreferHuman);

        Assert.IsTrue(result.CanAutoAcquire);
        Assert.AreEqual(synthetic, result.Winner);
    }

    [TestMethod]
    public void HumanOnlyRejectsKnownSyntheticNarration()
    {
        var synthetic = Option("a", narration: NarrationKind.Synthetic);

        var result = AudiobookCandidateSelector.Select([synthetic], AudiobookNarrationPreference.HumanOnly);

        Assert.IsFalse(result.CanAutoAcquire);
        Assert.IsEmpty(result.CandidatesRequiringNarrationConfirmation);
    }

    [TestMethod]
    public void HumanOnlyDoesNotTreatUnknownNarrationAsSatisfyingIt()
    {
        var unknown = Option("a", narration: NarrationKind.Unknown);

        var result = AudiobookCandidateSelector.Select([unknown], AudiobookNarrationPreference.HumanOnly);

        Assert.IsFalse(result.CanAutoAcquire);
        Assert.HasCount(1, result.CandidatesRequiringNarrationConfirmation);
        Assert.IsTrue(result.CandidatesRequiringNarrationConfirmation[0].RequiresNarrationConfirmation);
    }

    [TestMethod]
    public void NoPreferenceIgnoresNarrationTypeAndFallsThroughToLaterDimensions()
    {
        // Narration must contribute nothing under NoPreference -- the winner
        // comes from the next dimension (here, popularity) regardless of
        // which candidate is Human.
        var human = Option("a", narration: NarrationKind.Human, downloads: 10);
        var synthetic = Option("b", narration: NarrationKind.Synthetic, downloads: 10_000);

        var result = AudiobookCandidateSelector.Select([human, synthetic], AudiobookNarrationPreference.NoPreference);

        Assert.AreEqual(synthetic, result.Winner);
    }

    [TestMethod]
    public void PopularityCannotOvercomeAbridgement()
    {
        var complete = Option("a", downloads: 25);
        var abridged = Option("b", downloads: 100_000, isAbridged: true);

        var result = AudiobookCandidateSelector.Select([complete, abridged], AudiobookNarrationPreference.NoPreference);

        Assert.AreEqual(complete, result.Winner);
    }

    [TestMethod]
    public void FilePartCountAloneDoesNotDetermineTheWinner()
    {
        var fewerCompleteParts = Option("a", downloads: 10, partCount: 44);
        var morePartsButAbridged = Option("b", downloads: 100_000, partCount: 155, isAbridged: true);

        var result = AudiobookCandidateSelector.Select(
            [fewerCompleteParts, morePartsButAbridged], AudiobookNarrationPreference.NoPreference);

        Assert.AreEqual(fewerCompleteParts, result.Winner);
    }

    [TestMethod]
    public void MultipleAcceptableRecordsAutoSelectWithoutForcingAReview()
    {
        var human = Option("a", narration: NarrationKind.Human);
        var synthetic = Option("b", narration: NarrationKind.Synthetic);

        var result = AudiobookCandidateSelector.Select([human, synthetic], AudiobookNarrationPreference.PreferHuman);

        Assert.IsTrue(result.CanAutoAcquire);
        Assert.AreEqual(human, result.Winner);
        Assert.IsEmpty(result.CandidatesRequiringNarrationConfirmation);
    }

    [TestMethod]
    public void CandidatesTiedOnEveryModeledDimensionResolveDeterministicallyByStableId()
    {
        var first = Option("a");
        var second = Option("b");

        var resultOne = AudiobookCandidateSelector.Select([second, first], AudiobookNarrationPreference.NoPreference);
        var resultTwo = AudiobookCandidateSelector.Select([first, second], AudiobookNarrationPreference.NoPreference);

        Assert.AreEqual(first, resultOne.Winner);
        Assert.AreEqual(first, resultTwo.Winner);
    }

    [TestMethod]
    public void PackagingPreferenceBreaksATieWhenNarrationAndCompletenessAreEqual()
    {
        var mp3 = Option("a", format: "mp3");
        var m4b = Option("b", format: "m4b");

        var result = AudiobookCandidateSelector.Select([mp3, m4b], AudiobookNarrationPreference.NoPreference);

        Assert.AreEqual(m4b, result.Winner);
    }

    private static FulfillmentOption Option(
        string providerResultId,
        string format = "mp3",
        NarrationKind? narration = null,
        string? narrator = null,
        bool? isAbridged = null,
        int? partCount = null,
        int? downloads = null) => new(
        "gutendex", providerResultId, Guid.Empty, null,
        RequestMediaType.Audiobook, OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload,
        format, "en", null, null, 0m, null, "Public domain", null, null, null,
        PartCount: partCount,
        ProviderPopularity: downloads,
        IsAbridged: isAbridged,
        NarrationKind: narration,
        Narrator: narrator);
}
