using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// Synthetic, license-free artifact builders for M1-0. All titles, authors and
/// series are fictional; no real acquisition source is named.
/// </summary>
internal static class CharacterizationFixtures
{
    public const string Title = "The Lantern Road";
    public const string Author = "Mara Quill";

    public static MediaAsset Asset(RequestMediaType type, string format, Guid? requestFormatId = null) => new(
        Guid.NewGuid(), editionId: null, type, format, $"book{format}", $"stored{format}", sizeBytes: 1,
        new string('a', 64), type == RequestMediaType.Ebook ? "application/epub+zip" : "audio/mp4",
        requestFormatId ?? Guid.NewGuid(), sourceAcquisitionCandidateId: null, DateTimeOffset.UtcNow);

    public sealed class StubWorkLookup(string title, string author) : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, title, author, []));
    }

    public sealed class NoRequestStore : IBookRequestFulfillmentStore
    {
        public Task<BookRequest?> FindByFormatIdAsync(Guid formatId, CancellationToken cancellationToken) =>
            Task.FromResult<BookRequest?>(null);
    }

    /// <summary>
    /// A structurally valid EPUB. <paramref name="spineChapters"/> content files are
    /// zipped and listed in the spine; <paramref name="tocEntries"/> navigation
    /// entries are written (front matter and epilogue count as entries).
    /// </summary>
    public static MemoryStream Epub(
        string title, string author, string? language, int spineChapters, int tocEntries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Write(archive, "META-INF/container.xml",
                """<?xml version="1.0"?><container><rootfiles><rootfile full-path="OPS/content.opf" /></rootfiles></container>""");
            const string dc = "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"";
            var manifest = string.Concat(Enumerable.Range(1, spineChapters)
                .Select(i => $"<item id=\"c{i}\" href=\"c{i}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
            var spine = string.Concat(Enumerable.Range(1, spineChapters).Select(i => $"<itemref idref=\"c{i}\"/>"));
            Write(archive, "OPS/content.opf",
                $"""<?xml version="1.0"?><package><metadata><dc:title {dc}>{title}</dc:title><dc:creator {dc}>{author}</dc:creator>{(language is null ? "" : $"<dc:language {dc}>{language}</dc:language>")}</metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>""");
            Write(archive, "OPS/nav.xhtml",
                "<html><body><nav>" + string.Concat(Enumerable.Range(1, tocEntries).Select(i => $"<a href=\"c{Math.Min(i, Math.Max(spineChapters, 1))}.xhtml\">Entry {i}</a>")) + "</nav></body></html>");
            for (var i = 1; i <= spineChapters; i++) Write(archive, $"OPS/c{i}.xhtml", $"<html><body><h1>Chapter {i}</h1><p>Text.</p></body></html>");
        }

        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive archive, string name, string text, CompressionLevel level = CompressionLevel.Fastest)
    {
        var entry = archive.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    /// <summary>An M4B with the given <c>ilst</c> tags (atom <c>[0xA9]name</c> or <c>aART</c>).</summary>
    public static MemoryStream M4b(params (string Name, string Value)[] tags)
    {
        var items = tags.Select(tag => Tag(tag.Name, tag.Value)).SelectMany(bytes => bytes).ToArray();
        var moov = Box("moov", Box("udta", Box("meta", Concat(new byte[4], Box("ilst", items)))));
        return new MemoryStream(Concat(Box("ftyp", "M4B "u8.ToArray()), moov, Box("mdat", new byte[4096])));
    }

    private static byte[] Tag(string name, string value)
    {
        var atomName = name == "aART" ? name : "©" + name;
        var payload = Concat(BitConverter.GetBytes(1).Reverse().ToArray(), new byte[4], Encoding.UTF8.GetBytes(value));
        return Box(atomName, Box("data", payload), latin1Name: true);
    }

    private static byte[] Box(string type, byte[] payload, bool latin1Name = false)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(payload.Length + 8));
        return Concat(size, (latin1Name ? Encoding.Latin1 : Encoding.ASCII).GetBytes(type), payload);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
