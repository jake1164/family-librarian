using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Infrastructure.Tests.Characterization;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// M1-3b: deterministic completeness evidence for EPUBs. Evidence only: the identity verdict is the
/// M1-0 baseline and must never depend on it, no chapter-count or length threshold exists, and the
/// absence of every cue is never recorded as completeness.
/// </summary>
[TestClass]
public sealed class EpubCompletenessEvidenceTests
{
    private const string Opf = "http://www.idpf.org/2007/opf";

    private static EpubAssetIdentityVerifier Verifier(string title = Title) =>
        new(new StubWorkLookup(title, Author), new DeterministicBookMatcher(), new NoRequestStore());

    private static string Package(
        string title, string extraMetadata, IEnumerable<(string Id, string Href, string Properties)> manifest, IEnumerable<string> spine) =>
        $"""
        <?xml version="1.0"?>
        <package xmlns="{Opf}" version="3.0">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title>{title}</dc:title><dc:creator>{Author}</dc:creator><dc:language>en</dc:language>{extraMetadata}
          </metadata>
          <manifest>{string.Concat(manifest.Select(item => $"<item id=\"{item.Id}\" href=\"{item.Href}\" media-type=\"application/xhtml+xml\"{(item.Properties.Length > 0 ? $" properties=\"{item.Properties}\"" : "")}/>"))}</manifest>
          <spine>{string.Concat(spine.Select(id => $"<itemref idref=\"{id}\"/>"))}</spine>
        </package>
        """;

    private static MemoryStream Build(string packageXml, params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add(archive, "META-INF/container.xml",
                """<?xml version="1.0"?><container><rootfiles><rootfile full-path="OEBPS/content.opf"/></rootfiles></container>""");
            Add(archive, "OEBPS/content.opf", packageXml);
            foreach (var (name, content) in files) Add(archive, name, content);
        }

        stream.Position = 0;
        return stream;
    }

    private static void Add(ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Fastest)
    {
        var entry = archive.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Nav(IEnumerable<string> hrefs) =>
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav>" +
        string.Concat(hrefs.Select(href => $"<a href=\"{href}\">Entry</a>")) + "</nav></body></html>";

    /// <summary>A whole book of <paramref name="chapters"/> spine items plus a navigation document with the given links.</summary>
    private static MemoryStream Book(
        int chapters, IEnumerable<string>? navHrefs = null, string extraMetadata = "", string title = Title,
        IEnumerable<string>? spineIds = null, int filesPresent = int.MaxValue, string? navContent = null)
    {
        var ids = Enumerable.Range(1, chapters).Select(i => $"c{i}").ToArray();
        var manifest = ids.Select(id => (id, $"{id}.xhtml", "")).Append(("nav", "nav.xhtml", "nav"));
        var files = ids.Take(filesPresent).Select(id => ($"OEBPS/{id}.xhtml", "<html><body><p>Text.</p></body></html>"))
            .Append(("OEBPS/nav.xhtml", navContent ?? Nav(navHrefs ?? ids.Select(id => $"{id}.xhtml"))));
        return Build(Package(title, extraMetadata, manifest, spineIds ?? ids), files.ToArray());
    }

    private static async Task<AssetIdentityVerificationResult> Verify(MemoryStream epub, string requestedTitle = Title)
    {
        using (epub)
        {
            return await Verifier(requestedTitle).VerifyAsync(Asset(RequestMediaType.Ebook, ".epub"), epub, CancellationToken.None);
        }
    }

    private static IdentityFact[] Completeness(AssetIdentityVerificationResult result) =>
        result.Facts.Where(fact => fact.Dimension == EvidenceDimension.Completeness).ToArray();

    // --- full books and short legitimate books are unchanged -------------------------------------------------

    [TestMethod]
    public async Task AFullBookWhoseNavigationHasMoreEntriesThanChaptersRaisesNoCue()
    {
        // Journey D shape: front matter and chapters all present; 136 navigation entries for 135 chapters.
        var ids = Enumerable.Range(1, 136).Select(i => $"c{i}").ToArray();
        var result = await Verify(Book(136, navHrefs: ids.Select(id => $"{id}.xhtml")));

        Assert.IsTrue(result.IsMatch, "Identity verdict is unchanged.");
        var completeness = Completeness(result);
        Assert.AreEqual("spine=136;spineResolved=136;navLinks=136", completeness.Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.ReadingOrderCounted).Value);
        Assert.IsFalse(completeness.Any(f => f.Polarity == EvidencePolarity.Condition), "A count mismatch is not a cue; targets are what matter.");
    }

    [TestMethod]
    public async Task AShortLegitimateSingleChapterBookIsNotFlaggedAndStaysAMatch()
    {
        var result = await Verify(Book(1));

        Assert.IsTrue(result.IsMatch);
        Assert.IsFalse(Completeness(result).Any(f => f.Polarity == EvidencePolarity.Condition),
            "No minimum chapter count or length exists; a novella is a complete book.");
    }

    [TestMethod]
    public async Task ACleanSampleWithIntactStructureIsNotDetectableAndNeverRecordedAsComplete()
    {
        // The E02 limit, stated honestly: three intact chapters, clean metadata, no marker.
        using var epub = CharacterizationFixtures.Epub(Title, Author, "en", 3, 3);
        var result = await Verifier().VerifyAsync(Asset(RequestMediaType.Ebook, ".epub"), epub, CancellationToken.None);

        Assert.IsTrue(result.IsMatch, "E02 baseline unchanged: identity still matches.");
        var noCue = Completeness(result).Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.NoCueDetected);
        Assert.AreEqual(EvidencePolarity.Unknown, noCue.Polarity, "Absence of cues is not evidence of completeness.");
        Assert.IsFalse(Completeness(result).Any(f => f.Polarity == EvidencePolarity.Positive));
    }

    // --- the cues --------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("<dc:subject xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Free Sample</dc:subject>", "subject:sample")]
    [DataRow("<dc:subject xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Fantasy</dc:subject><dc:type xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Excerpt</dc:type>", "type:excerpt")]
    [DataRow("<dc:subject xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Book Preview</dc:subject>", "subject:preview")]
    [DataRow("<dc:subject xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Teaser chapters</dc:subject>", "subject:teaser")]
    public async Task AnExplicitSampleMarkerInMetadataIsRecordedAsACueAndTheVerdictIsUnchanged(string metadata, string expected)
    {
        var marked = await Verify(Book(3, extraMetadata: metadata));
        var plain = await Verify(Book(3));

        var cue = Completeness(marked).Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata);
        Assert.AreEqual(EvidencePolarity.Condition, cue.Polarity);
        Assert.AreEqual(expected, cue.Value);
        Assert.AreEqual(plain.IsMatch, marked.IsMatch, "The cue never changes the identity verdict.");
    }

    [TestMethod]
    public async Task ASampleWordInTheTitleIsACueUnlessItBelongsToTheRequestedWork()
    {
        var marked = await Verify(Book(3, title: "The Lantern Road (Sample)"));
        Assert.IsTrue(Completeness(marked).Any(f => f.Value == "title:sample"));

        // A book that is genuinely titled with the word is not flagged for it.
        var named = await Verify(Book(3, title: "The Sample Season"), requestedTitle: "The Sample Season");
        Assert.IsFalse(Completeness(named).Any(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata));
    }

    [TestMethod]
    public async Task NavigationLinksToMissingFilesAreRecordedWithTheirCounts()
    {
        var hrefs = Enumerable.Range(1, 7).Select(i => $"c{i}.xhtml").Concat(["gone1.xhtml", "gone2.xhtml#x", "text/gone3.xhtml"]);
        var result = await Verify(Book(7, navHrefs: hrefs));

        var cue = Completeness(result).Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing);
        Assert.AreEqual("3/10", cue.Value);
        Assert.AreEqual(EvidencePolarity.Condition, cue.Polarity);
        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task SameDocumentFragmentLinksAreNotCountedAsMissingTargets()
    {
        var result = await Verify(Book(2, navHrefs: ["#top", "c1.xhtml#a", "c2.xhtml"]));

        Assert.IsFalse(Completeness(result).Any(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing));
        StringAssert.Contains(Completeness(result).Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.ReadingOrderCounted).Value, "navLinks=2");
    }

    [TestMethod]
    public async Task ReadingOrderItemsWhoseFilesAreMissingAreRecordedAndTheBookStillPassesStructure()
    {
        using var epub = Book(5, filesPresent: 3);
        var bytes = epub.ToArray();
        var structure = await new EpubValidator().ValidateAsync(
            Asset(RequestMediaType.Ebook, ".epub"), new MemoryStream(bytes), CancellationToken.None);
        var result = await Verify(new MemoryStream(bytes));

        Assert.IsTrue(structure.IsValid, "M1-3a is unchanged: some content resolves, so it is not a no-reading-order failure.");
        Assert.AreEqual("2/5", Completeness(result).Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.SpineContentMissing).Value);
    }

    [TestMethod]
    public async Task TheSameFileListedTwiceInTheReadingOrderIsRecorded()
    {
        var result = await Verify(Book(2, spineIds: ["c1", "c2", "c1"]));

        Assert.AreEqual("1", Completeness(result).Single(f => f.Code == EvidenceReasonCodes.ArtifactCompleteness.DuplicateReadingOrderItems).Value);
    }

    // --- safety and unchanged behavior -------------------------------------------------------------------------

    [TestMethod]
    public async Task UnreadableNavigationNeverChangesTheVerdictOrThrows()
    {
        var plain = await Verify(Book(3));
        var broken = await Verify(Book(3, navContent: "<<< not xml"));

        Assert.AreEqual(plain.IsMatch, broken.IsMatch);
        Assert.IsEmpty(Completeness(broken), "Best effort: no completeness evidence rather than a failure.");
    }

    [TestMethod]
    public async Task EmptySpineStillFailsStructureExactlyAsInM13a()
    {
        using var empty = CharacterizationFixtures.Epub(Title, Author, "en", 0, 0);
        var bytes = empty.ToArray();

        var structure = await new EpubValidator().ValidateAsync(
            Asset(RequestMediaType.Ebook, ".epub"), new MemoryStream(bytes), CancellationToken.None);

        Assert.IsFalse(structure.IsValid, "M1-3a hard failure is preserved.");
        Assert.AreEqual(EpubValidator.NoReadingOrderMessage, structure.Message);
    }

    [TestMethod]
    public async Task OtherFormatsGainNoCompletenessEvidence()
    {
        var m4bVerifier = new M4bAssetIdentityVerifier(new StubWorkLookup(Title, Author), new DeterministicBookMatcher());
        using var m4b = CharacterizationFixtures.M4b(("nam", Title), ("ART", Author));
        var audio = await m4bVerifier.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);

        Assert.IsTrue(audio.IsMatch);
        Assert.IsFalse(audio.Facts.Any(f => f.Dimension == EvidenceDimension.Completeness));
        Assert.IsFalse(Verifier().Supports(Asset(RequestMediaType.Audiobook, ".mp3")));
        Assert.IsFalse(Verifier().Supports(Asset(RequestMediaType.Ebook, ".mobi")));
    }

    [TestMethod]
    public async Task CompletenessFindingsAreRecordedInTheirOwnDimensionAndNeverAsAnIdentityContradiction()
    {
        var result = await Verify(Book(3, extraMetadata: "<dc:subject xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Sample</dc:subject>"));
        var store = new RecordingStore();
        var recorder = new ArtifactEvidenceRecorder(store, new FixedClock());

        await recorder.RecordIdentityVerificationAsync(
            Asset(RequestMediaType.Ebook, ".epub"), result with { Facts = [.. result.Facts, new IdentityFact(
                "ev-extra", EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Negative, "1/2")] },
            CancellationToken.None);

        var completeness = store.Saved.Where(item => item.Dimension == EvidenceDimension.Completeness).ToArray();
        Assert.IsTrue(completeness.Any(item => item.Code == EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata && item.Polarity == EvidencePolarity.Condition));
        Assert.AreEqual("artifact.identity_corroboration.corroborated", store.Saved.Single(item => item.FindingId == "ev-corroboration").Code,
            "A negative completeness fact must not turn identity corroboration into a contradiction.");
        Assert.IsTrue(store.Saved.All(item => (item.Value?.Length ?? 0) <= ArtifactEvidence.MaxValueLength));
        var ids = store.Saved.Select(item => item.FindingId).ToArray();
        Assert.HasCount(ids.Length, ids.Distinct().ToArray());
    }

    private sealed class RecordingStore : IArtifactEvidenceStore
    {
        private readonly List<ArtifactEvidence> _pending = [];

        public List<ArtifactEvidence> Saved { get; } = [];

        public void Add(IReadOnlyList<ArtifactEvidence> evidence) => _pending.AddRange(evidence);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
        {
            Saved.AddRange(_pending);
            _pending.Clear();
            return Task.FromResult(true);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
            IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(new Dictionary<Guid, IReadOnlyList<ArtifactEvidence>>());
    }

    private sealed class FixedClock : FamilyLibrarian.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    }
}
