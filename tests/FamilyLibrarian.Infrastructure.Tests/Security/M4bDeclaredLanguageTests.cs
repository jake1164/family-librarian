using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Infrastructure.Tests.Characterization;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// M1-4a (A02): an audiobook whose audio track explicitly declares a language other than the one accepted
/// for the request is held as <c>Unmatched</c> before it can be trusted. Only the standard container fields
/// (media header and extended language tag) of audio tracks count. A missing, undetermined, unrecognized or
/// unreadable value is unknown, never a match and never a mismatch. Nothing is detected from the audio.
/// </summary>
[TestClass]
public sealed class M4bDeclaredLanguageTests
{
    private static M4bAssetIdentityVerifier Verifier(IBookRequestFulfillmentStore? store = null) =>
        new(new StubWorkLookup(Title, Author), new DeterministicBookMatcher(), store);

    private static async Task<AssetIdentityVerificationResult> Verify(
        byte[][] tracks, (string, string)[]? tags = null, IBookRequestFulfillmentStore? store = null, Guid? formatId = null)
    {
        using var m4b = M4bWithTracks(tracks, tags ?? [("nam", Title), ("ART", Author)]);
        return await Verifier(store).VerifyAsync(
            Asset(RequestMediaType.Audiobook, ".m4b", formatId), m4b, CancellationToken.None);
    }

    private static IdentityFact? Language(AssetIdentityVerificationResult result) =>
        result.Facts.SingleOrDefault(fact => fact.Dimension == EvidenceDimension.Language);

    // --- explicit contradictions are held ------------------------------------------------------------------

    [TestMethod]
    [DataRow("deu", "mdhd 639-2/T")]
    [DataRow("ger", "mdhd 639-2/B alias")]
    [DataRow("fra", "mdhd French")]
    public async Task ADeclaredLanguageThatContradictsTheAcceptedLanguageIsHeld(string code, string _)
    {
        var result = await Verify([Track(mdhdLanguage: code)]);

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "declared language");
        StringAssert.Contains(result.Reason, code);
        var language = Language(result)!;
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.DeclaredConflicts, language.Code);
        Assert.AreEqual(EvidencePolarity.Negative, language.Polarity);
        Assert.AreEqual(code, language.Value);
        Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, result.Facts.Single(f => f.FindingId == "ev-title").Code,
            "Title and author evidence is untouched; the language is its own dimension.");
    }

    [TestMethod]
    public async Task AVersionOneMediaHeaderIsReadToo()
    {
        var result = await Verify([Track(mdhdLanguage: "spa", mdhdVersion: 1)]);

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("spa", Language(result)!.Value);
    }

    [TestMethod]
    public async Task TheExtendedLanguageTagWinsOverTheMediaHeader()
    {
        var conflicting = await Verify([Track(mdhdLanguage: "eng", elng: "de-DE")]);
        var matching = await Verify([Track(mdhdLanguage: "deu", elng: "en-US")]);

        Assert.IsFalse(conflicting.IsMatch);
        Assert.AreEqual("de-DE", Language(conflicting)!.Value);
        Assert.IsTrue(matching.IsMatch);
        Assert.AreEqual("en-US", Language(matching)!.Value);
    }

    [TestMethod]
    public async Task ATitleMismatchAndALanguageConflictAreBothExplained()
    {
        var result = await Verify([Track(mdhdLanguage: "deu")], tags: [("nam", "The Ash Gate"), ("ART", Author)]);

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "embedded title");
        StringAssert.Contains(result.Reason, "declared language");
    }

    // --- agreement keeps the existing accepted path ----------------------------------------------------------

    [TestMethod]
    [DataRow("eng")]
    [DataRow("en")]
    public async Task ADeclaredLanguageMatchingTheAcceptedLanguageKeepsTheExistingMatch(string code)
    {
        var result = await Verify([Track(mdhdLanguage: code.Length == 3 ? code : null, elng: code.Length == 3 ? null : code)]);

        Assert.IsTrue(result.IsMatch);
        var language = Language(result)!;
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.DeclaredMatchesRequest, language.Code);
        Assert.AreEqual(EvidencePolarity.Positive, language.Polarity);
    }

    [TestMethod]
    public async Task TheLanguageAcceptedForTheRequestDecidesNotAHardcodedEnglish()
    {
        var store = new AcceptedLanguageStore("de");

        var german = await Verify([Track(mdhdLanguage: "deu")], store: store, formatId: store.FormatId);
        var english = await Verify([Track(mdhdLanguage: "eng")], store: store, formatId: store.FormatId);

        Assert.IsTrue(german.IsMatch, "A German book accepted for this request is not held for being German.");
        Assert.IsFalse(english.IsMatch);
    }

    // --- unknown is never a match and never a mismatch ---------------------------------------------------------

    [TestMethod]
    [DataRow("und")]
    [DataRow("zxx")]
    [DataRow("mul")]
    [DataRow("mis")]
    public async Task AnUndeterminedLanguageIsUnknownNotAMismatchAndNotPositive(string code)
    {
        var result = await Verify([Track(mdhdLanguage: code)]);

        Assert.IsTrue(result.IsMatch, "Existing behavior: no usable language does not block a title/author match.");
        AssertUnknownLanguage(result);
    }

    [TestMethod]
    public async Task AFileWithNoAudioTrackLanguageAtAllIsUnknown()
    {
        using var m4b = CharacterizationFixtures.M4b(("nam", Title), ("ART", Author));
        var result = await Verifier().VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        AssertUnknownLanguage(result);
    }

    [TestMethod]
    public async Task AnUnrecognizedLanguageCodeIsUnknownNotAMismatch()
    {
        var result = await Verify([Track(mdhdLanguage: "qxz")]);

        Assert.IsTrue(result.IsMatch);
        AssertUnknownLanguage(result);
    }

    [TestMethod]
    public async Task ANonStandardLanguageTagIsIgnored()
    {
        using var m4b = CharacterizationFixtures.M4b(("nam", Title), ("ART", Author), ("lng", "German"));
        var result = await Verifier().VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.IsTrue(result.IsMatch, "A freeform tag is not a trustworthy language field.");
        AssertUnknownLanguage(result);
    }

    [TestMethod]
    public async Task OnlyAudioTracksAreConsidered()
    {
        var result = await Verify([Track(handler: "text", mdhdLanguage: "deu"), Track(mdhdLanguage: "eng")]);

        Assert.IsTrue(result.IsMatch, "A chapter-text track's language must not hold an English audiobook.");
        Assert.AreEqual("eng", Language(result)!.Value);
        var onlyText = await Verify([Track(handler: "text", mdhdLanguage: "deu")]);
        Assert.IsTrue(onlyText.IsMatch);
        AssertUnknownLanguage(onlyText);
    }

    [TestMethod]
    public async Task AnUnreadableTrackLeavesTheLanguageNotAssessedAndTheTagVerdictUnchanged()
    {
        // moov holds valid tags first, then a track box that claims to be larger than the container.
        var items = Concat(Tag("nam", Title), Tag("ART", Author));
        var udta = Box("udta", Box("meta", Concat(new byte[4], Box("ilst", items))));
        var badTrack = new byte[] { 0, 0, 3, 232, (byte)'t', (byte)'r', (byte)'a', (byte)'k' };
        var moov = Box("moov", Concat(udta, badTrack));
        using var m4b = new MemoryStream(Concat(Box("ftyp", "M4B "u8.ToArray()), moov, Box("mdat", new byte[64])));

        var result = await Verifier().VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.IsNull(Language(result), "No language fact at all: the recorder stores 'not assessed'.");
    }

    [TestMethod]
    public async Task ATitleAndAuthorMatchWithNoTracksStaysExactlyAsBefore()
    {
        var result = await Verify([]);

        Assert.IsTrue(result.IsMatch);
        Assert.IsNull(result.Reason);
    }

    private static void AssertUnknownLanguage(AssetIdentityVerificationResult result)
    {
        var language = Language(result)!;
        Assert.AreEqual(EvidenceReasonCodes.ArtifactLanguage.NotDeclared, language.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, language.Polarity);
        Assert.IsNull(language.Value);
    }

    /// <summary>A request whose single audiobook format has accepted the given language, as a librarian's "get it anyway" does.</summary>
    private sealed class AcceptedLanguageStore : IBookRequestFulfillmentStore
    {
        private readonly BookRequest _request;

        public AcceptedLanguageStore(string language)
        {
            _request = new BookRequest(Guid.NewGuid(), Guid.NewGuid(), [RequestMediaType.Audiobook], null, DateTimeOffset.UtcNow);
            var format = _request.Formats.Single();
            FormatId = format.Id;
            _request.MarkNeedsReview(
                RequestReviewCategory.PreferenceAmbiguity, "Found, but not in English.", DateTimeOffset.UtcNow,
                [new RequestReviewCandidateInput(format.Id, "gutenberg", "1", Title, Author, language, null, null, null, false)]);
            _request.AcceptReviewCandidate(_request.ReviewCandidates.Single().Id, actorUserId: null, DateTimeOffset.UtcNow);
        }

        public Guid FormatId { get; }

        public Task<BookRequest?> FindByFormatIdAsync(Guid formatId, CancellationToken cancellationToken) =>
            Task.FromResult<BookRequest?>(formatId == FormatId ? _request : null);
    }
}
