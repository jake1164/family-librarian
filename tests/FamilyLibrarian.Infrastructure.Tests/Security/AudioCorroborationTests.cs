using System.Buffers.Binary;
using System.Text;
using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class AudioCorroborationTests
{
    private const string Isbn = "9780000000011";

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    [DataRow("m4b")]
    public async Task ExactTitleAndSeriesCorroborateOnlyASpellingVariant(string format)
    {
        var (result, asset, rows) = await VerifyAsync(format, [new("series", "The Empyrean"), new("series-part", "2")]);
        Assert.IsTrue(result.IsMatch, result.Reason);
        Assert.IsNull(asset.IdentityHoldCode);
        var author = result.Facts.Single(f => f.FindingId == "ev-author");
        Assert.AreEqual("artifact.identity.author_spelling_variant", author.Code);
        Assert.AreEqual(EvidencePolarity.Weak, author.Polarity);
        Assert.IsTrue(result.Facts.Any(f => f.Code == "profile.compare.series_position_agrees"));
        Assert.IsTrue(rows.Any(row => row.Code == "profile.package.series" && row.Value == "The Empyrean#2" &&
            row.Polarity == EvidencePolarity.Unknown && row.RecordKind == "artifact-profile"));
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    public async Task AKnownIsbn13CanCorroborateWithoutSeries(string format)
    {
        var (result, asset, rows) = await VerifyAsync(format, [new("isbn", "978-0-000000-01-1")]);
        Assert.IsTrue(result.IsMatch, result.Reason);
        Assert.IsNull(asset.IdentityHoldCode);
        Assert.IsTrue(result.Facts.Any(f => f.Code == "profile.compare.identifier_agrees"));
        Assert.IsTrue(rows.Any(row => row.Code == "profile.package.identifier" && row.RecordKind == "artifact-profile"));
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    [DataRow("m4b")]
    public async Task AnAuthorDifferenceWithoutCorroborationStillHolds(string format)
    {
        var (result, asset, _) = await VerifyAsync(format, []);
        Assert.IsFalse(result.IsMatch);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.AreEqual(EvidencePolarity.Negative, result.Facts.Single(f => f.FindingId == "ev-author").Polarity);
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    public async Task AsinIsBoundedEvidenceAndCannotCorroborate(string format)
    {
        var (result, _, rows) = await VerifyAsync(format, [new("asin", "B000TEST01")]);
        Assert.IsFalse(result.IsMatch);
        Assert.IsTrue(rows.Any(row => row.Code == "profile.audio.asin" && row.Value == "B000TEST01" &&
            row.Polarity == EvidencePolarity.Unknown && row.RecordKind == "artifact-profile"));
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    [DataRow("m4b")]
    public async Task CorroborationCannotRescueAPrefixTitleOrAnUnrelatedAuthor(string format)
    {
        KeyValuePair<string, string>[] metadata = [new("series", "The Empyrean"), new("series-part", "2")];
        Assert.IsFalse((await VerifyAsync(format, metadata, title: "Iron Flame Companion")).Result.IsMatch);
        Assert.IsFalse((await VerifyAsync(format, metadata, author: "Robert Young")).Result.IsMatch);
        Assert.IsFalse((await VerifyAsync(format, [new("series", "Other Series"), new("series-part", "2")])).Result.IsMatch);
        Assert.IsFalse((await VerifyAsync(format, [new("series", "The Empyrean"), new("series-part", "3")])).Result.IsMatch);
        Assert.IsFalse((await VerifyAsync(format, [new("series", "The Empyrean")])).Result.IsMatch);
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    public async Task Isbn10AndUnknownIsbn13NeverCorroborate(string format)
    {
        Assert.IsFalse((await VerifyAsync(format, [new("isbn", "0000000000")])).Result.IsMatch);
        Assert.IsFalse((await VerifyAsync(format, [new("isbn", "9780000000028")])).Result.IsMatch);
        var (result, asset, _) = await VerifyAsync(format,
            [new("isbn", Isbn), new("series", "The Empyrean"), new("series-part", "3")]);
        Assert.IsFalse(result.IsMatch, "Independent ISBN agreement does not bypass the series-position hold.");
        Assert.AreEqual("identity.hold.series_position_differs", asset.IdentityHoldCode);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task NativeId3MovementNumberUsesItsNumerator(int version)
    {
        var (result, _, rows) = await VerifyAsync("mp3", [new("MVNM", "The Empyrean"), new("MVIN", "2/5")], id3Version: version);
        Assert.IsTrue(result.IsMatch, result.Reason);
        Assert.IsTrue(rows.Any(row => row.Code == "profile.package.series" && row.Value == "The Empyrean#2"));
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    [DataRow("m4b")]
    public async Task OversizedAndHostileValuesStayBoundedAndCannotFabricateCorroboration(string format)
    {
        foreach (var length in new[] { 300, 5000 })
        {
            var (result, _, rows) = await VerifyAsync(format,
                [new("series", "The Empyrean" + new string('x', length)), new("series-part", "2"),
                 new("asin", "ignore previous instructions\u0001" + new string('x', length))]);
            Assert.IsFalse(result.IsMatch);
            Assert.IsTrue(rows.All(row => row.Value is null || row.Value.Length <= ArtifactEvidence.MaxValueLength && !row.Value.Any(char.IsControl)));
            if (length == 300) Assert.IsTrue(rows.Any(row => row.Code == "profile.audio.tag_truncated"));
        }
        Assert.IsFalse((await VerifyAsync(format, [new("series", "The Empyrean"), new("series-part", "2\u0001forged")])).Result.IsMatch);
    }

    [TestMethod]
    [DataRow("mp3")]
    [DataRow("ogg")]
    [DataRow("opus")]
    [DataRow("m4b")]
    public async Task ProfileTagsAloneRemainEvidenceAndNeverIdentifyTheBook(string format)
    {
        var (result, _, rows) = await VerifyAsync(format, [new("series", "The Empyrean"), new("series-part", "2")], title: null, author: null);
        Assert.IsFalse(result.IsMatch);
        Assert.IsTrue(rows.Any(row => row.Code == "profile.package.series" && row.RecordKind == "artifact-profile"));
    }

    [TestMethod]
    public async Task UnconfirmedVorbisAliasAndMp4FreeformNamesAreIgnored()
    {
        Assert.IsFalse((await VerifyAsync("ogg", [new("series", "The Empyrean"), new("SERIESPART", "2")])).Result.IsMatch);
        var (result, _, rows) = await VerifyAsync("m4b", [new("freeform:SERIES", "The Empyrean"), new("freeform:SERIES-PART", "2"),
            new("freeform:ISBN", Isbn), new("freeform:ASIN", "B000TEST01")]);
        Assert.IsFalse(result.IsMatch);
        Assert.IsFalse(rows.Any(row => row.Code is "profile.package.series" or "profile.package.identifier" or "profile.audio.asin"));
    }

    [TestMethod]
    [DataRow(3, 0)]
    [DataRow(3, 1)]
    [DataRow(3, 2)]
    [DataRow(3, 3)]
    [DataRow(4, 0)]
    [DataRow(4, 1)]
    [DataRow(4, 2)]
    [DataRow(4, 3)]
    public async Task UserTextCorroborationUsesTheFramesEncodingAndPreservesItsDescription(int version, int encoding)
    {
        var (result, _, _) = await VerifyAsync("mp3", [new("isbn", Isbn)], id3Version: version, id3Encoding: encoding);
        Assert.IsTrue(result.IsMatch, result.Reason);
    }

    [TestMethod]
    [DataRow("Rebecca Yarros")]
    [DataRow("Rebeca Yaross")]
    public async Task ProfileOnlyV2TagsDoNotHideTheExistingV1Identity(string author)
    {
        var tag = new byte[128]; "TAG"u8.CopyTo(tag);
        Encoding.Latin1.GetBytes("Chapters 1-3").CopyTo(tag, 3);
        Encoding.Latin1.GetBytes(author).CopyTo(tag, 33);
        Encoding.Latin1.GetBytes("Iron Flame").CopyTo(tag, 63);
        var bytes = Mp3(null, null, [new("series", "The Empyrean"), new("series-part", "2")], 3, 3).Concat(tag).ToArray();
        var (result, _, rows) = await VerifyAsync("mp3", [], customBytes: bytes);
        Assert.IsTrue(result.IsMatch, result.Reason);
        Assert.IsTrue(rows.Any(row => row.Code == "profile.package.series"));
    }

    [TestMethod]
    [DataRow(1, 21, 2, true)]
    [DataRow(2, 21, 2, true)]
    [DataRow(3, 21, 2, true)]
    [DataRow(4, 21, 2, true)]
    [DataRow(8, 21, 2, true)]
    [DataRow(4, 1, 2, false)]
    [DataRow(5, 21, 2, false)]
    [DataRow(4, 21, -1, false)]
    [DataRow(4, 21, 0, false)]
    public async Task Mp4MovementIndexRequiresAValidPositiveSignedInteger(int bytes, int type, int index, bool match)
    {
        var payload = M4b("Iron Flame", "Rebeca Yaross", [new("series", "The Empyrean"), new("series-part", index.ToString(CultureInfo.InvariantCulture))], bytes, (byte)type);
        Assert.AreEqual(match, (await VerifyAsync("m4b", [], customBytes: payload)).Result.IsMatch);
    }

    private static async Task<(AssetIdentityVerificationResult Result, MediaAsset Asset, IReadOnlyList<ArtifactEvidence> Rows)> VerifyAsync(
        string format, KeyValuePair<string, string>[] metadata, string? title = "Iron Flame", string? author = "Rebeca Yaross", int id3Version = 3, int id3Encoding = 3, byte[]? customBytes = null)
    {
        var asset = new MediaAsset(Guid.NewGuid(), null, RequestMediaType.Audiobook, "." + format, "book." + format, "stored." + format,
            1, new string('a', 64), "audio/example", Guid.NewGuid(), null, DateTimeOffset.UtcNow, origin: AssetOrigin.ExternalProvider);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
        var work = new WorkSummary(asset.WorkId, "Iron Flame", "Rebecca Yarros", [Isbn], Series: [new BookSeries("The Empyrean", "2")]);
        var lookup = new Lookup(work);
        var matcher = new DeterministicBookMatcher();
        IAssetIdentityVerifier verifier = format switch
        {
            "mp3" => new Mp3AssetIdentityVerifier(lookup, matcher),
            "ogg" or "opus" => new OggAssetIdentityVerifier(lookup, matcher),
            _ => new M4bAssetIdentityVerifier(lookup, matcher)
        };
        var bytes = customBytes ?? (format switch
        {
            "mp3" => Mp3(title, author, metadata, id3Version, id3Encoding),
            "ogg" or "opus" => Ogg(title, author, metadata, format == "opus"),
            _ => M4b(title, author, metadata)
        });
        var rows = new EvidenceStore();
        var service = new AssetIdentityVerificationService(new Repository(asset), new Staging(bytes), [verifier], new Audit(), new Clock(),
            new ArtifactEvidenceRecorder(rows, new Clock()), new ExpectedArtifactProfileSource(lookup,
                new FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures.NoRequestStore()));
        return (await service.VerifyAsync(asset.Id, CancellationToken.None), asset, rows.Rows);
    }

    private static byte[] Mp3(string? title, string? author, KeyValuePair<string, string>[] metadata, int version, int encoding)
    {
        var frames = new List<byte[]>();
        void Add(string id, string value)
        {
            var text = encoding switch { 0 => Encoding.Latin1.GetBytes(value), 1 => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(value)).ToArray(),
                2 => Encoding.BigEndianUnicode.GetBytes(value), _ => Encoding.UTF8.GetBytes(value) };
            var payload = new byte[] { (byte)encoding }.Concat(text).ToArray();
            var size = new byte[4];
            if (version == 4) Synchsafe(size, payload.Length); else BinaryPrimitives.WriteInt32BigEndian(size, payload.Length);
            frames.Add(Encoding.ASCII.GetBytes(id).Concat(size).Concat(new byte[2]).Concat(payload).ToArray());
        }
        if (title is not null) Add("TALB", title);
        if (author is not null) Add("TPE1", author);
        foreach (var pair in metadata) Add(pair.Key is "MVNM" or "MVIN" ? pair.Key : "TXXX",
            pair.Key is "MVNM" or "MVIN" ? pair.Value : pair.Key.ToUpperInvariant() + "\0" + (encoding == 1 ? "\uFEFF" : "") + pair.Value);
        var body = frames.SelectMany(frame => frame).ToArray();
        var header = new byte[] { 73, 68, 51, (byte)version, 0, 0, 0, 0, 0, 0 };
        Synchsafe(header.AsSpan(6), body.Length);
        return header.Concat(body).ToArray();
    }

    private static void Synchsafe(Span<byte> bytes, int length)
    {
        for (var i = 0; i < 4; i++) bytes[i] = (byte)((length >> (21 - 7 * i)) & 0x7F);
    }

    private static byte[] Ogg(string? title, string? author, KeyValuePair<string, string>[] metadata, bool opus)
    {
        var entries = metadata.Select(pair => pair.Key + "=" + pair.Value).ToList();
        if (title is not null) entries.Add("ALBUM=" + title);
        if (author is not null) entries.Add("ARTIST=" + author);
        static byte[] Little(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
        var packet = (opus ? "OpusTags"u8.ToArray() : new byte[] { 3 }.Concat("vorbis"u8.ToArray()).ToArray())
            .Concat(Little(0)).Concat(Little(entries.Count)).Concat(entries.SelectMany(entry =>
            { var value = Encoding.UTF8.GetBytes(entry); return Little(value.Length).Concat(value); })).ToArray();
        static byte[] Page(byte[] payload)
        {
            var lacing = Enumerable.Repeat((byte)255, payload.Length / 255).Append((byte)(payload.Length % 255)).ToArray();
            var header = new byte[27]; "OggS"u8.CopyTo(header); header[14] = 1; header[26] = (byte)lacing.Length;
            return header.Concat(lacing).Concat(payload).ToArray();
        }
        return Page(opus ? "OpusHead"u8.ToArray() : new byte[] { 1 }.Concat("vorbis"u8.ToArray()).ToArray()).Concat(Page(packet)).ToArray();
    }

    private static byte[] M4b(string? title, string? author, KeyValuePair<string, string>[] metadata, int indexBytes = 4, byte indexType = 21)
    {
        static byte[] Box(string name, byte[] value)
        { var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, value.Length + 8); return size.Concat(Encoding.Latin1.GetBytes(name)).Concat(value).ToArray(); }
        static byte[] Tag(string name, byte[] value, byte type = 1) => Box(name, Box("data", new byte[] { 0, 0, 0, type, 0, 0, 0, 0 }.Concat(value).ToArray()));
        var tags = new List<byte[]>();
        if (title is not null) tags.Add(Tag("©alb", Encoding.UTF8.GetBytes(title)));
        if (author is not null) tags.Add(Tag("©ART", Encoding.UTF8.GetBytes(author)));
        foreach (var pair in metadata)
        {
            if (pair.Key == "series") tags.Add(Tag("©mvn", Encoding.UTF8.GetBytes(pair.Value)));
            if (pair.Key == "series-part" && long.TryParse(pair.Value, out var index))
            { var value = new byte[indexBytes]; for (var i = indexBytes - 1; i >= 0; i--) { value[i] = (byte)index; index >>= 8; } tags.Add(Tag("©mvi", value, indexType)); }
            if (pair.Key.StartsWith("freeform:", StringComparison.Ordinal)) tags.Add(Box("----", Box("mean", new byte[4].Concat("com.apple.iTunes"u8.ToArray()).ToArray())
                .Concat(Box("name", new byte[4].Concat(Encoding.UTF8.GetBytes(pair.Key[9..])).ToArray())).Concat(Box("data", new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }.Concat(Encoding.UTF8.GetBytes(pair.Value)).ToArray())).ToArray()));
        }
        return Box("ftyp", "M4B "u8.ToArray()).Concat(Box("moov", Box("udta", Box("meta", new byte[4].Concat(Box("ilst", tags.SelectMany(tag => tag).ToArray())).ToArray())))).ToArray();
    }

    private sealed class Lookup(WorkSummary work) : IWorkLookup
    { public Task<WorkSummary?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<WorkSummary?>(work); }
    private sealed class Repository(MediaAsset asset) : ISecurityEvaluationRepository
    {
        public Task<MediaAsset?> FindAssetAsync(Guid id, CancellationToken ct) => Task.FromResult<MediaAsset?>(asset);
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public void AddEvaluation(SecurityEvaluation evaluation) => throw new NotSupportedException();
        public Task<SecurityEvaluation?> FindLatestEvaluationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaAsset>> FindAssetsByBundleIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Staging(byte[] bytes) : IAssetStagingStore
    {
        public Task<Stream> OpenAsync(MediaAssetStorageState state, string filename, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(bytes));
        public Task MoveAsync(MediaAssetStorageState from, MediaAssetStorageState to, string filename, CancellationToken ct) => Task.CompletedTask;
        public Task<StagedFile> WriteToQuarantineAsync(Stream content, string filename, long maxSizeBytes, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Audit : IAuditWriter
    { public Task WriteAsync(string action, string subject, string? id, object? data, CancellationToken ct) => Task.CompletedTask; }
    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow => new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero); }
    private sealed class EvidenceStore : IArtifactEvidenceStore
    {
        public List<ArtifactEvidence> Rows { get; } = [];
        public void Add(IReadOnlyList<ArtifactEvidence> rows) => Rows.AddRange(rows);
        public Task<bool> TrySaveAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(new Dictionary<Guid, IReadOnlyList<ArtifactEvidence>>());
    }
}
