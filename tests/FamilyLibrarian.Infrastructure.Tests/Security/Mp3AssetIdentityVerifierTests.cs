using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// AUTOFIND 3a: an MP3's ID3 tags are compared with the Work so an external MP3 can clear on its content. Tag bytes are built
/// in the test; no binary fixture is committed.
/// </summary>
[TestClass]
public sealed class Mp3AssetIdentityVerifierTests
{
    private const string GermanBlurb =
        "Der ehemalige Navy SEAL James Reece kehrt nach einem Hinterhalt zurück, der sein gesamtes Team das Leben kostete. " +
        "Er will wissen, wer ihn verraten hat und warum es dazu kam. Das ist die Geschichte von einem Mann, " +
        "der nicht aufgibt und mit allen Mitteln für die Wahrheit kämpft.";

    private const string EnglishBlurb =
        "The story of a man who has been betrayed by the people that he trusted, and of the long road that he must take " +
        "to find out what happened to his team, and who was behind it, and why they did it to him.";

    // ---- parsing -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Id3v23LatinTextFramesAreRead()
    {
        var tags = await ReadAsync(Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"),
            Id3.Text("TLAN", "eng"), Id3.Text("TCON", "Thriller"))));

        Assert.IsNotNull(tags);
        Assert.AreEqual("id3v2.3", tags.Source);
        Assert.AreEqual("The Terminal List", tags.Album);
        AssertValues(tags.Artists, "Jack Carr");
        AssertValues(tags.Languages, "eng");
        Assert.AreEqual("Thriller", tags.Genre);
    }

    [TestMethod]
    public async Task Id3v22ThreeLetterFramesAreRead()
    {
        var tags = await ReadAsync(Mp3(Id3.Tag(2, Id3.Text("TAL", "The Terminal List", version: 2), Id3.Text("TP1", "Jack Carr", version: 2),
            Id3.Text("TLA", "eng", version: 2), Id3.Text("TT2", "Chapter 1", version: 2))));

        Assert.IsNotNull(tags);
        Assert.AreEqual("id3v2.2", tags.Source);
        Assert.AreEqual("The Terminal List", tags.Album);
        Assert.AreEqual("Chapter 1", tags.Title);
        AssertValues(tags.Artists, "Jack Carr");
    }

    [TestMethod]
    public async Task Id3v24Utf8FramesWithSeveralValuesAreSplit()
    {
        var tags = await ReadAsync(Mp3(Id3.Tag(4, Id3.Text("TPE1", "Jack Carr\0Josef Vossenkuhl", version: 4, encoding: 3),
            Id3.Text("TALB", "Die Abschussliste ungekürzt", version: 4, encoding: 3))));

        Assert.IsNotNull(tags);
        AssertValues(tags.Artists, "Jack Carr", "Josef Vossenkuhl");
        Assert.AreEqual("Die Abschussliste ungekürzt", tags.Album);
    }

    [TestMethod]
    public async Task Id3v24UnsynchronisedFrameIsRestored()
    {
        // 0xFF 0x00 in the stored frame is a single 0xFF once unsynchronisation is undone: "ÿ" in Latin-1.
        var stored = new byte[] { 0, (byte)'A', 0xFF, 0x00, (byte)'B' };
        var tags = await ReadAsync(Mp3(Id3.Tag(4, Id3.Frame("TALB", stored, version: 4, flags: 0x0002))));

        Assert.IsNotNull(tags);
        Assert.AreEqual("AÿB", tags.Album);
    }

    [TestMethod]
    public async Task Utf16WithAByteOrderMarkIsDecoded()
    {
        var tags = await ReadAsync(Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List", encoding: 1), Id3.Text("TPE1", "Jack Carr", encoding: 2))));

        Assert.IsNotNull(tags);
        Assert.AreEqual("The Terminal List", tags.Album);
        AssertValues(tags.Artists, "Jack Carr");
    }

    [TestMethod]
    public async Task Id3v1AloneIsReadAsAFallback()
    {
        var v1 = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(v1, 0);
        Encoding.Latin1.GetBytes("Chapter 1").CopyTo(v1, 3);
        Encoding.Latin1.GetBytes("Jack Carr").CopyTo(v1, 33);
        Encoding.Latin1.GetBytes("The Terminal List").CopyTo(v1, 63);

        var tags = await ReadAsync(Concat(new byte[2048], v1));

        Assert.IsNotNull(tags);
        Assert.AreEqual("id3v1", tags.Source);
        Assert.AreEqual("The Terminal List", tags.Album);
        Assert.AreEqual("Chapter 1", tags.Title);
        AssertValues(tags.Artists, "Jack Carr");
    }

    [TestMethod]
    public async Task ACommentFrameYieldsOnlyItsText()
    {
        var tags = await ReadAsync(Mp3(Id3.Tag(3, Id3.Comment(GermanBlurb))));

        Assert.IsNotNull(tags);
        Assert.AreEqual(GermanBlurb, tags.Comment);
    }

    // ---- hostile input -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AHostileTagSizeIsTreatedAsNoTag()
    {
        var header = Concat("ID3"u8.ToArray(), [3, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F], new byte[64]);

        Assert.IsNull(await ReadAsync(header));
    }

    [TestMethod]
    public async Task ANonSynchsafeSizeIsTreatedAsNoTag()
    {
        var header = Concat("ID3"u8.ToArray(), [3, 0, 0, 0x80, 0, 0, 0x10], new byte[64]);

        Assert.IsNull(await ReadAsync(header));
    }

    [TestMethod]
    public async Task ATextFrameClaimingTenMebibytesIsSkippedWithoutReadingIt()
    {
        var hostile = Id3.RawFrame("TALB", 10 * 1024 * 1024, new byte[] { 0, (byte)'A' });
        var tags = await ReadAsync(Mp3(Id3.Tag(3, Id3.Text("TPE1", "Jack Carr"), hostile)));

        Assert.IsNotNull(tags);
        Assert.IsNull(tags.Album);
        AssertValues(tags.Artists, "Jack Carr");
    }

    [TestMethod]
    public async Task AnOversizedTextFrameThatFitsTheTagIsSkippedNotRead()
    {
        var big = Id3.Text("TALB", new string('x', 5000));
        var tags = await ReadAsync(Mp3(Id3.Tag(3, big, Id3.Text("TPE1", "Jack Carr"))));

        Assert.IsNotNull(tags);
        Assert.IsNull(tags.Album, "A text frame over 4 KiB is skipped.");
        AssertValues(tags.Artists, "Jack Carr");
    }

    [TestMethod]
    public async Task ATruncatedFrameProducesNoTagAndNoException()
    {
        var truncated = Id3.RawFrame("TALB", 200, new byte[] { 0, (byte)'A' });
        var tags = await ReadAsync(Mp3(Id3.Tag(3, truncated)));

        Assert.IsNull(tags);
    }

    [TestMethod]
    public async Task ATagShorterThanItsDeclaredSizeIsNoTag()
    {
        var tag = Id3.Tag(3, Id3.Text("TALB", "The Terminal List"));
        var cut = tag[..(tag.Length - 8)];

        Assert.IsNull(await ReadAsync(cut));
    }

    [TestMethod]
    public async Task AttachedPicturesAndUnknownFramesAreSkippedBySize()
    {
        var picture = Id3.Frame("APIC", new byte[200_000]);
        var tags = await ReadAsync(Mp3(Id3.Tag(3, picture, Id3.Text("TALB", "The Terminal List"))));

        Assert.IsNotNull(tags);
        Assert.AreEqual("The Terminal List", tags.Album);
    }

    [TestMethod]
    public async Task RandomBytesNeverThrow()
    {
        var random = new Random(7);
        for (var run = 0; run < 200; run++)
        {
            var bytes = new byte[random.Next(0, 4096)];
            random.NextBytes(bytes);
            if (run % 2 == 0 && bytes.Length > 10)
            {
                "ID3"u8.CopyTo(bytes);
                bytes[3] = (byte)random.Next(2, 5);
                for (var index = 6; index < 10; index++) bytes[index] &= 0x0F;
            }

            await ReadAsync(bytes);
        }
    }

    // ---- verification --------------------------------------------------------------------------------------------

    [TestMethod]
    public void OnlyMp3FilesAreSupported()
    {
        var verifier = CreateVerifier("The Terminal List", "Jack Carr");

        Assert.IsTrue(verifier.Supports(Asset(RequestMediaType.Audiobook, ".mp3")));
        Assert.IsTrue(verifier.Supports(Asset(RequestMediaType.Audiobook, ".MP3")));
        Assert.IsFalse(verifier.Supports(Asset(RequestMediaType.Audiobook, ".m4b")));
    }

    [TestMethod]
    public async Task MatchingAlbumAndAuthorWithADeclaredAcceptedLanguageMatch()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TLAN", "eng"))));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-title" && fact.Polarity == EvidencePolarity.Positive));
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-author" && fact.Polarity == EvidencePolarity.Positive));
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-language" && fact.Polarity == EvidencePolarity.Positive));
        Assert.AreEqual("id3v2.3", result.Facts.Single(fact => fact.FindingId == "ev-tag-source").Value);
    }

    [TestMethod]
    public async Task ANarratorOnlyArtistWithAMatchingAlbumArtistStillMatches()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Ray Porter"), Id3.Text("TPE2", "Jack Carr"))));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
    }

    [TestMethod]
    public async Task TheCatalogAuthorQualifierIsIgnored()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Carr, Jack (Joint pseudonym)"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"))));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
    }

    [TestMethod]
    public async Task ADifferentAlbumIsAContradictionAndUnmatched()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "Fourth Wing"), Id3.Text("TPE1", "Jack Carr"))));

        Assert.IsFalse(result.IsMatch);
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-title" && fact.Polarity == EvidencePolarity.Negative));
    }

    [TestMethod]
    public async Task ADifferentAuthorIsAContradictionAndUnmatched()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Someone Else"))));

        Assert.IsFalse(result.IsMatch);
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-author" && fact.Polarity == EvidencePolarity.Negative));
    }

    [TestMethod]
    public async Task ATrackTitleAloneIsUnknownNeverNegativeAndNeverClears()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TIT2", "Chapter 01"), Id3.Text("TPE1", "Jack Carr"))),
            AssetFor(AssetOrigin.ExternalProvider));

        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId, "Exactly the unverifiable-format outcome.");
        var title = result.Facts.Single(fact => fact.FindingId == "ev-title");
        Assert.AreEqual(EvidencePolarity.Unknown, title.Polarity);
        Assert.IsFalse(result.Facts.Any(fact => fact.Polarity == EvidencePolarity.Negative));
    }

    [TestMethod]
    public async Task AMatchingAlbumWithNoAuthorTagDoesNotClear()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"))));

        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-title" && fact.Polarity == EvidencePolarity.Positive));
        Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-author" && fact.Polarity == EvidencePolarity.Unknown));
    }

    [TestMethod]
    public async Task NoTagsAtAllReturnsExactlyTheNoVerifierOutcomeWithATagsAbsentFact()
    {
        foreach (var origin in new[] { AssetOrigin.BuiltInProvider, AssetOrigin.ExternalProvider })
        {
            var asset = AssetFor(origin);
            var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"), new byte[4096], asset);
            var expected = NoVerifierOutcome.For(asset);

            Assert.AreEqual(expected.IsMatch, result.IsMatch);
            Assert.AreEqual(expected.VerifierId, result.VerifierId);
            Assert.AreEqual(expected.Reason, result.Reason);
            Assert.AreEqual(
                EvidenceReasonCodes.ArtifactIdentityCorroboration.TagsAbsent,
                result.Facts.Single().Code);
            Assert.AreEqual(EvidencePolarity.Unknown, result.Facts.Single().Polarity);
        }
    }

    [TestMethod]
    public async Task ADeclaredLanguageThatConflictsIsUnmatchedWithALanguageFact()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TLAN", "ger"))));

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
        Assert.IsTrue(result.Facts.Any(fact => fact.Dimension == EvidenceDimension.Language && fact.Polarity == EvidencePolarity.Negative));
        StringAssert.Contains(result.Reason, "declared language");
    }

    [TestMethod]
    [DataRow("und")]
    [DataRow("zxx")]
    [DataRow("xx-not-a-language")]
    public async Task AnUnspecifiedOrUnrecognizedDeclaredLanguageIsUnknownNotAMismatch(string language)
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TLAN", language))));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
    }

    [TestMethod]
    public async Task ACueWordInTheAlbumThatConflictsWithTheRequestIsUnmatched()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List (ungekürzt)"), Id3.Text("TPE1", "Jack Carr"))));

        Assert.IsFalse(result.IsMatch);
        var cue = result.Facts.Single(fact => fact.Code == EvidenceReasonCodes.ArtifactLanguage.CueConflicts);
        Assert.AreEqual(EvidencePolarity.Negative, cue.Polarity);
        Assert.AreEqual("cue:UNGEKÜRZT", cue.Value);
    }

    [TestMethod]
    public async Task AGermanAlbumWithACueWordIsUnmatched()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "Die Abschussliste (ungekürzt)"), Id3.Text("TPE1", "Jack Carr"))));

        Assert.IsFalse(result.IsMatch);
    }

    [TestMethod]
    public async Task ACueWordInTheFileNameIsAlsoRead()
    {
        var asset = AssetFor(AssetOrigin.BuiltInProvider, "Jack Carr - The Terminal List - Hörbuch.mp3");
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"))), asset);

        Assert.IsFalse(result.IsMatch);
    }

    [TestMethod]
    public async Task TheRealLabShapeIsNotClearedBecauseItsDescriptionIsGerman()
    {
        // Verified on the lab: ID3v2.3, TALB begins with the English title, narrator in TCOM, German COMM and TCON,
        // no TLAN, and no cue word anywhere.
        var labShape = Mp3(Id3.Tag(3,
            Id3.Text("TALB", "THE TERMINAL LIST - Die Abschussliste"),
            Id3.Text("TPE1", "Jack Carr"), Id3.Text("TPE2", "Jack Carr"),
            Id3.Text("TCOM", "Josef Vossenkuhl"),
            Id3.Text("TCON", "Spionage-Thriller; Thriller"),
            Id3.Comment(GermanBlurb)));

        foreach (var origin in new[] { AssetOrigin.ExternalProvider, AssetOrigin.BuiltInProvider })
        {
            var asset = AssetFor(origin);
            var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"), labShape, asset);
            var expected = NoVerifierOutcome.For(asset);

            Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId, "Exactly the unverifiable-format outcome.");
            Assert.AreEqual(expected.IsMatch, result.IsMatch);
            Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-title" && fact.Polarity == EvidencePolarity.Positive));
            Assert.IsTrue(result.Facts.Any(fact => fact.FindingId == "ev-author" && fact.Polarity == EvidencePolarity.Positive));
            var inferred = result.Facts.Single(fact => fact.FindingId == "ev-language-inferred");
            Assert.AreEqual("inferred:de", inferred.Value);
            Assert.AreEqual(EvidencePolarity.Negative, inferred.Polarity);
        }
    }

    [TestMethod]
    public async Task AnEnglishDescriptionAgreesAndAllowsClearing()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Comment(EnglishBlurb))));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
        var inferred = result.Facts.Single(fact => fact.FindingId == "ev-language-inferred");
        Assert.AreEqual("inferred:en", inferred.Value);
        Assert.AreEqual(EvidencePolarity.Positive, inferred.Polarity);
    }

    [TestMethod]
    public async Task ShortOrMixedTextInfersNothingAndDoesNotBlockClearing()
    {
        const string mixed = "The man and the dog went to the park with his hat. Der Mann und das Kind sind nicht da, " +
            "er hat sie mit dem Hund gesehen und von dort aus nach Hause.";
        foreach (var text in new[] { "Ein kurzer Satz.", mixed })
        {
            var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
                Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Comment(text))));

            Assert.IsTrue(result.IsMatch, text);
            Assert.AreEqual("mp3-id3-tags", result.VerifierId);
            Assert.IsFalse(result.Facts.Any(fact => fact.FindingId == "ev-language-inferred"), text);
        }
    }

    [TestMethod]
    public async Task ADeclaredLanguageAlwaysWinsOverInference()
    {
        var result = await VerifyAsync(CreateVerifier("The Terminal List", "Jack Carr"),
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TLAN", "eng"), Id3.Comment(GermanBlurb))));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
        Assert.IsFalse(result.Facts.Any(fact => fact.FindingId == "ev-language-inferred"));
    }

    [TestMethod]
    public async Task ACatalogEntryWithNoTitleBehavesAsBeforeThisVerifierExisted()
    {
        var asset = AssetFor(AssetOrigin.ExternalProvider);
        var verifier = new Mp3AssetIdentityVerifier(new Lookup("", "Jack Carr"), new DeterministicBookMatcher(), new NoRequestStore());

        var result = await verifier.VerifyAsync(asset,
            new MemoryStream(Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List")))), CancellationToken.None);

        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
    }

    // ---- through the real service and hold policy ----------------------------------------------------------------

    [TestMethod]
    public async Task AnExternalTrackWhoseTagsMatchIsNotHeld()
    {
        var (service, asset) = Service(AssetOrigin.ExternalProvider,
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TLAN", "eng"))));

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("mp3-id3-tags", result.VerifierId);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
        Assert.IsNull(asset.IdentityHoldCode);
    }

    [TestMethod]
    public async Task TheRealLabShapeFromAnExternalProviderStaysHeldAsAttestedOnly()
    {
        var (service, asset) = Service(AssetOrigin.ExternalProvider, Mp3(Id3.Tag(3,
            Id3.Text("TALB", "THE TERMINAL LIST - Die Abschussliste"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TPE2", "Jack Carr"),
            Id3.Text("TCOM", "Josef Vossenkuhl"), Id3.Text("TCON", "Spionage-Thriller; Thriller"), Id3.Comment(GermanBlurb))));

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.AreEqual("identity.hold.attested_only_external", asset.IdentityHoldCode);
        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
    }

    [TestMethod]
    public async Task TheRealLabShapeFromABuiltInProviderKeepsItsAttestedMatch()
    {
        var (service, asset) = Service(AssetOrigin.BuiltInProvider, Mp3(Id3.Tag(3,
            Id3.Text("TALB", "THE TERMINAL LIST - Die Abschussliste"), Id3.Text("TPE1", "Jack Carr"),
            Id3.Text("TCON", "Spionage-Thriller; Thriller"), Id3.Comment(GermanBlurb))));

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual(ArtifactEvidenceRecorder.NoVerifierId, result.VerifierId);
        Assert.AreEqual(MediaAssetStorageState.Processing, asset.StorageState);
    }

    [TestMethod]
    public async Task ADeclaredGermanLanguageHoldsWithTheLanguageConflictCode()
    {
        var (service, asset) = Service(AssetOrigin.ExternalProvider,
            Mp3(Id3.Tag(3, Id3.Text("TALB", "The Terminal List"), Id3.Text("TPE1", "Jack Carr"), Id3.Text("TLAN", "ger"))));

        var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual("identity.hold.language_conflict", asset.IdentityHoldCode);
    }

    [TestMethod]
    public async Task ATrackWithNoTagsFromABuiltInProviderIsStillTrustedAndFromAnExternalOneStillHeld()
    {
        var (builtInService, builtIn) = Service(AssetOrigin.BuiltInProvider, new byte[4096]);
        Assert.IsTrue((await builtInService.VerifyAsync(builtIn.Id, CancellationToken.None)).IsMatch);

        var (externalService, external) = Service(AssetOrigin.ExternalProvider, new byte[4096]);
        await externalService.VerifyAsync(external.Id, CancellationToken.None);
        Assert.AreEqual("identity.hold.attested_only_external", external.IdentityHoldCode);
    }

    [TestMethod]
    public async Task HostileTagBytesNeverEscapeTheService()
    {
        foreach (var hostile in new[]
                 {
                     Concat("ID3"u8.ToArray(), [3, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F], new byte[64]),
                     Mp3(Id3.Tag(3, Id3.RawFrame("TALB", 10 * 1024 * 1024, new byte[] { 0, (byte)'A' }))),
                     Mp3(Id3.Tag(3, Id3.RawFrame("TALB", 200, new byte[] { 0, (byte)'A' })))
                 })
        {
            var (service, asset) = Service(AssetOrigin.ExternalProvider, hostile);
            var result = await service.VerifyAsync(asset.Id, CancellationToken.None);

            Assert.IsFalse(result.IsMatch);
            Assert.AreEqual("identity.hold.attested_only_external", asset.IdentityHoldCode);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static void AssertValues(IEnumerable<string> actual, params string[] expected) =>
        CollectionAssert.AreEqual(expected, actual.ToArray());

    private static async Task<Id3Tags?> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        return await Id3TagReader.ReadAsync(stream, CancellationToken.None);
    }

    private static MediaAsset AssetFor(AssetOrigin origin, string fileName = "book.mp3") => new(
        Guid.NewGuid(), null, RequestMediaType.Audiobook, ".mp3", fileName, "stored.mp3", 1, new string('a', 64), "audio/mpeg",
        Guid.NewGuid(), null, DateTimeOffset.UtcNow, identityPreConfirmed: true, origin: origin,
        originUserId: origin == AssetOrigin.ManualUpload ? Guid.NewGuid() : null);

    private static Mp3AssetIdentityVerifier CreateVerifier(string title, string author) =>
        new(new Lookup(title, author), new DeterministicBookMatcher(), new NoRequestStore());

    private static Task<AssetIdentityVerificationResult> VerifyAsync(
        Mp3AssetIdentityVerifier verifier, byte[] bytes, MediaAsset? asset = null) =>
        verifier.VerifyAsync(asset ?? AssetFor(AssetOrigin.BuiltInProvider), new MemoryStream(bytes), CancellationToken.None);

    private static (AssetIdentityVerificationService Service, MediaAsset Asset) Service(AssetOrigin origin, byte[] bytes)
    {
        var asset = AssetFor(origin);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
        var service = new AssetIdentityVerificationService(
            new Repository(asset), new Staging(bytes),
            [new Mp3AssetIdentityVerifier(new Lookup("The Terminal List", "Jack Carr"), new DeterministicBookMatcher(), new NoRequestStore())],
            new Audit(), new Clock());
        return (service, asset);
    }

    /// <summary>A tag followed by 4 KiB of non-tag bytes standing in for audio.</summary>
    private static byte[] Mp3(byte[] tag) => Concat(tag, new byte[4096]);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class Lookup(string title, string author) : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, title, author, []));
    }

    private sealed class Repository(MediaAsset asset) : ISecurityEvaluationRepository
    {
        public Task<MediaAsset?> FindAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(asset.Id == assetId ? asset : null);

        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid bundleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid assetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Staging(byte[] bytes) : IAssetStagingStore
    {
        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenAsync(MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes));

        public Task MoveAsync(
            MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class Audit : IAuditWriter
    {
        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Builds ID3v2 tag bytes. Sizes are written exactly as the format defines them.</summary>
    private static class Id3
    {
        public static byte[] Tag(int version, params byte[][] frames)
        {
            var body = frames.SelectMany(frame => frame).ToArray();
            return Concat("ID3"u8.ToArray(), [(byte)version, 0, 0], Synchsafe(body.Length), body);
        }

        public static byte[] Text(string id, string text, int version = 3, byte encoding = 0)
        {
            var payload = encoding switch
            {
                1 => Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes(text)),
                2 => Encoding.BigEndianUnicode.GetBytes(text),
                3 => Encoding.UTF8.GetBytes(text),
                _ => Encoding.Latin1.GetBytes(text)
            };
            return Frame(id, Concat([encoding], payload), version);
        }

        /// <summary>A v2.3 comment: UTF-8, language, empty description, then the text.</summary>
        public static byte[] Comment(string text) =>
            Frame("COMM", Concat([3], "deu"u8.ToArray(), [0], Encoding.UTF8.GetBytes(text)));

        public static byte[] Frame(string id, byte[] data, int version = 3, int flags = 0)
        {
            if (version == 2)
            {
                return Concat(Encoding.ASCII.GetBytes(id), [(byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length], data);
            }

            var size = version == 4
                ? Synchsafe(data.Length)
                : [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length];
            return Concat(Encoding.ASCII.GetBytes(id), size, [(byte)(flags >> 8), (byte)flags], data);
        }

        /// <summary>A v2.3 frame whose declared size is not the size of the bytes that follow.</summary>
        public static byte[] RawFrame(string id, int declaredSize, byte[] data) => Concat(
            Encoding.ASCII.GetBytes(id),
            [(byte)(declaredSize >> 24), (byte)(declaredSize >> 16), (byte)(declaredSize >> 8), (byte)declaredSize],
            [0, 0], data);

        private static byte[] Synchsafe(int value) =>
            [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];
    }
}
