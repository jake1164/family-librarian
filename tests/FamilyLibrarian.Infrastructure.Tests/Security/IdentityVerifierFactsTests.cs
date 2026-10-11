using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Infrastructure.Tests.Characterization;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// M1-2: the EPUB and M4B verifiers now report the observations they already make as facts. The
/// verdicts are the M1-0 baseline and must be unchanged; facts only add evidence.
/// </summary>
[TestClass]
public sealed class IdentityVerifierFactsTests
{
    private static readonly EpubAssetIdentityVerifier Epub =
        new(new StubWorkLookup(Title, Author), new DeterministicBookMatcher(), new NoRequestStore());
    private static readonly M4bAssetIdentityVerifier M4b =
        new(new StubWorkLookup(Title, Author), new DeterministicBookMatcher());

    private static async Task<FamilyLibrarian.Application.Security.AssetIdentityVerificationResult> VerifyEpub(
        string title, string author, string? language)
    {
        using var epub = Epub_(title, author, language);
        return await Epub.VerifyAsync(Asset(RequestMediaType.Ebook, ".epub"), epub, CancellationToken.None);
    }

    private static MemoryStream Epub_(string title, string author, string? language) =>
        CharacterizationFixtures.Epub(title, author, language, 5, 5);

    [TestMethod]
    public async Task ACorrectEpubReportsAgreeingTitleAuthorAndAcceptedLanguage()
    {
        var result = await VerifyEpub(Title, Author, "en");

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, result.Facts.Single(f => f.FindingId == "ev-title").Code);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorAgrees, result.Facts.Single(f => f.FindingId == "ev-author").Code);
        var language = result.Facts.Single(f => f.Dimension == EvidenceDimension.Language);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.DeclaredMatchesRequest, language.Code);
        Assert.AreEqual("en", language.Value);
    }

    [TestMethod]
    public async Task AWrongLanguageEpubStaysUnmatchedAndReportsAConflictingDeclaredLanguage()
    {
        var result = await VerifyEpub(Title, Author, "de");

        Assert.IsFalse(result.IsMatch, "E05 baseline: unchanged.");
        var language = result.Facts.Single(f => f.Dimension == EvidenceDimension.Language);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.DeclaredConflicts, language.Code);
        Assert.AreEqual(EvidencePolarity.Negative, language.Polarity);
        Assert.AreEqual("de", language.Value);
    }

    [TestMethod]
    public async Task AnEpubWithoutALanguageStaysMatchedAndReportsNotDeclaredAsUnknown()
    {
        var result = await VerifyEpub(Title, Author, null);

        Assert.IsTrue(result.IsMatch, "E06 baseline: unchanged.");
        var language = result.Facts.Single(f => f.Dimension == EvidenceDimension.Language);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.NotDeclared, language.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, language.Polarity);
    }

    [TestMethod]
    public async Task ADifferentAuthorStaysUnmatchedAndReportsTheDifference()
    {
        var result = await VerifyEpub(Title, "Mara Quinn", "en");

        Assert.IsFalse(result.IsMatch, "E07 baseline: unchanged.");
        Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorDiffers, result.Facts.Single(f => f.FindingId == "ev-author").Code);
        Assert.AreEqual(EvidencePolarity.Negative, result.Facts.Single(f => f.FindingId == "ev-author").Polarity);
    }

    [TestMethod]
    public async Task AnM4bReportsTitleAndAuthorFactsAndAnUnknownLanguageWhenNoStandardFieldIsDeclared()
    {
        using var matching = M4b_(("nam", Title), ("ART", Author));
        var match = await M4b.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), matching, CancellationToken.None);

        Assert.IsTrue(match.IsMatch);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, match.Facts.Single(f => f.FindingId == "ev-title").Code);
        // M1-4a: the M4B verifier now reads the standard audio-track language field. With none declared the
        // language is Unknown (never Positive); it is not guessed from anything else.
        var language = match.Facts.Single(f => f.Dimension == EvidenceDimension.Language);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.NotDeclared, language.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, language.Polarity);

        // A non-standard freeform language tag is not a trustworthy field: still a match, still unknown.
        using var german = M4b_(("nam", Title), ("ART", Author), ("lng", "German"));
        var stillMatches = await M4b.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), german, CancellationToken.None);
        Assert.IsTrue(stillMatches.IsMatch);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.NotDeclared, stillMatches.Facts.Single(f => f.Dimension == EvidenceDimension.Language).Code);
    }

    [TestMethod]
    public async Task AnM4bWithADifferentTitleStaysUnmatchedWithANegativeTitleFact()
    {
        using var neighbor = M4b_(("nam", "The Ash Gate"), ("ART", Author));
        var result = await M4b.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), neighbor, CancellationToken.None);

        Assert.IsFalse(result.IsMatch, "A03 baseline: unchanged.");
        Assert.AreEqual(EvidencePolarity.Negative, result.Facts.Single(f => f.FindingId == "ev-title").Polarity);
    }

    [TestMethod]
    public async Task AnM4bWithNoTagsReportsNoFactsAndStaysUnmatched()
    {
        using var empty = M4b_();
        var result = await M4b.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), empty, CancellationToken.None);

        Assert.IsFalse(result.IsMatch, "A04 baseline: unchanged.");
        Assert.IsEmpty(result.Facts, "Unreadable is inconclusive, not a contradiction.");
    }

    private static MemoryStream M4b_(params (string Name, string Value)[] tags) => CharacterizationFixtures.M4b(tags);
}
