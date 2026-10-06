using System.Buffers.Binary;
using System.Text;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class M4bAssetIdentityVerifierTests
{
    private const string Copyright = "©";

    [TestMethod]
    public async Task OnlyM4bFilesAreSupported()
    {
        var verifier = CreateVerifier("Fourth Wing", "Rebecca Yarros");

        Assert.IsTrue(verifier.Supports(CreateAsset(".m4b")));
        Assert.IsTrue(verifier.Supports(CreateAsset(".M4B")));
        Assert.IsFalse(verifier.Supports(CreateAsset(".mp3")));
        Assert.IsFalse(verifier.Supports(CreateAsset(".epub")));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MatchingTitleAndAuthorTagsAreAccepted()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Fourth Wing"), Tag("ART", "Rebecca Yarros")));

        Assert.IsTrue(result.IsMatch);
        Assert.AreEqual("m4b-tag-metadata", result.VerifierId);
    }

    [TestMethod]
    public async Task AMoovBoxAfterTheMediaPayloadIsFound()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(moovFirst: false, Tag("nam", "Fourth Wing"), Tag("ART", "Rebecca Yarros")));

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task TheAlbumTagCanCarryTheTitle()
    {
        var result = await VerifyAsync(
            CreateVerifier("Threshing Day", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Chapter 1"), Tag("alb", "Threshing Day"), Tag("wrt", "Rebecca Yarros")));

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task AnAuthorHeldInAnyOfTheAuthorTagsIsAccepted()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Fourth Wing"), Tag("ART", "Rebecca Soler"), Tag("aART", "Rebecca Yarros")));

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task ATitleWithoutAnyAuthorTagIsAcceptedOnTitleAlone()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Fourth Wing")));

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task ATitleMatchingARecordedEditionTitleIsAccepted()
    {
        var result = await VerifyAsync(
            CreateVerifier("Mekhanicheskii apelsin", "Anthony Burgess", alternateTitles: ["A Clockwork Orange"]),
            BuildM4b(Tag("nam", "A Clockwork Orange"), Tag("ART", "Anthony Burgess")));

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task ADifferentTitleIsHeldAndTheReasonNamesBothTitles()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Iron Flame"), Tag("ART", "Rebecca Yarros")));

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "Iron Flame");
        StringAssert.Contains(result.Reason, "Fourth Wing");
    }

    [TestMethod]
    public async Task AnAuthorTagThatMatchesNobodyIsHeld()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Fourth Wing"), Tag("ART", "Someone Else")));

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "Someone Else");
        StringAssert.Contains(result.Reason, "Rebecca Yarros");
    }

    [TestMethod]
    public async Task AFileWithAuthorTagsButNoTitleIsHeld()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("ART", "Rebecca Yarros")));

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "no embedded title");
    }

    [TestMethod]
    public async Task AFileWithNoTagListIsHeld()
    {
        var result = await VerifyAsync(CreateVerifier("Fourth Wing", "Rebecca Yarros"), BuildM4b());

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "no embedded title or author");
    }

    [TestMethod]
    public async Task ANonUtf8TagValueIsIgnored()
    {
        var result = await VerifyAsync(
            CreateVerifier("Fourth Wing", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Fourth Wing", typeIndicator: 21)));

        Assert.IsFalse(result.IsMatch);
    }

    [TestMethod]
    public async Task ATruncatedContainerIsHeldWithoutThrowing()
    {
        // moov last, so cutting the file's tail cuts into moov itself.
        var full = BuildM4b(moovFirst: false, Tag("nam", "Fourth Wing"));
        using var truncated = new MemoryStream(full.ToArray()[..^6]);

        var result = await VerifyAsync(CreateVerifier("Fourth Wing", "Rebecca Yarros"), truncated);

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "malformed");
    }

    [TestMethod]
    public async Task ABoxClaimingMoreThanItsParentHoldsIsRejected()
    {
        // A udta child whose header declares far more bytes than the file holds.
        var hugeChild = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(hugeChild, 0x7FFFFFFF);
        "meta"u8.CopyTo(hugeChild.AsSpan(4));
        using var stream = new MemoryStream(
            Concat(Box("ftyp", "M4B "u8.ToArray()), Box("moov", Box("udta", hugeChild))));

        var result = await VerifyAsync(CreateVerifier("Fourth Wing", "Rebecca Yarros"), stream);

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "malformed");
    }

    [TestMethod]
    public async Task ASixtyFourBitBoxSizeIsHandled()
    {
        var tags = Box("ilst", Concat(Tag("nam", "Fourth Wing"), Tag("ART", "Rebecca Yarros")));
        var meta = Box("meta", Concat(new byte[4], tags));
        using var stream = new MemoryStream(
            Concat(Box("ftyp", "M4B "u8.ToArray()), LargeBox("moov", Box("udta", meta))));

        var result = await VerifyAsync(CreateVerifier("Fourth Wing", "Rebecca Yarros"), stream);

        Assert.IsTrue(result.IsMatch);
    }

    [TestMethod]
    public async Task AWorkWithNoTitleIsHeld()
    {
        var result = await VerifyAsync(
            CreateVerifier("", "Rebecca Yarros"),
            BuildM4b(Tag("nam", "Fourth Wing")));

        Assert.IsFalse(result.IsMatch);
        StringAssert.Contains(result.Reason, "no title on file");
    }

    private static Task<AssetIdentityVerificationResult> VerifyAsync(
        M4bAssetIdentityVerifier verifier, Stream content) =>
        verifier.VerifyAsync(CreateAsset(".m4b"), content, CancellationToken.None);

    private static M4bAssetIdentityVerifier CreateVerifier(
        string title, string author, IReadOnlyList<string>? alternateTitles = null) =>
        new(new StubWorkLookup(title, author, alternateTitles), new DeterministicBookMatcher());

    private static MediaAsset CreateAsset(string format) => new(
        Guid.NewGuid(),
        editionId: null,
        RequestMediaType.Audiobook,
        format,
        $"book{format}",
        $"stored{format}",
        sizeBytes: 1,
        new string('a', 64),
        "audio/mp4",
        Guid.NewGuid(),
        sourceAcquisitionCandidateId: null,
        DateTimeOffset.UtcNow);

    /// <summary>One <c>ilst</c> item: atom <c>[0xA9]name</c> (or <c>aART</c>) holding a <c>data</c> atom.</summary>
    private static byte[] Tag(string name, string value, int typeIndicator = 1)
    {
        var atomName = name == "aART" ? name : Copyright + name;
        var payload = Concat(
            BitConverter.GetBytes(typeIndicator).Reverse().ToArray(),
            new byte[4],
            Encoding.UTF8.GetBytes(value));
        return Box(atomName, Box("data", payload), latin1Name: true);
    }

    private static MemoryStream BuildM4b(params byte[][] tags) => BuildM4b(moovFirst: true, tags);

    private static MemoryStream BuildM4b(bool moovFirst, params byte[][] tags)
    {
        var moov = tags.Length == 0
            ? Box("moov", Box("mvhd", new byte[100]))
            : Box("moov", Box("udta", Box("meta", Concat(new byte[4], Box("ilst", Concat(tags))))));
        var ftyp = Box("ftyp", "M4B "u8.ToArray());
        var mdat = Box("mdat", new byte[4096]);
        return new MemoryStream(moovFirst ? Concat(ftyp, moov, mdat) : Concat(ftyp, mdat, moov));
    }

    private static byte[] Box(string type, byte[] payload, bool latin1Name = false)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(payload.Length + 8));
        return Concat(size, (latin1Name ? Encoding.Latin1 : Encoding.ASCII).GetBytes(type), payload);
    }

    /// <summary>A box using the 64-bit "largesize" form (size field == 1).</summary>
    private static byte[] LargeBox(string type, byte[] payload)
    {
        var largeSize = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(largeSize, (ulong)(payload.Length + 16));
        return Concat(BitConverter.GetBytes(1).Reverse().ToArray(), Encoding.ASCII.GetBytes(type), largeSize, payload);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class StubWorkLookup(
        string title, string author, IReadOnlyList<string>? alternateTitles = null) : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, title, author, [], AlternateTitles: alternateTitles));
    }
}
