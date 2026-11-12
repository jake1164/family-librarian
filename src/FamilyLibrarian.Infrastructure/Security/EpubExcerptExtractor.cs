using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

public sealed class EpubExcerptExtractor(IAssetStagingStore staging) : IArtifactExcerptExtractor
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private const string Ops = "http://www.idpf.org/2007/ops";
    private static readonly string[] FrontTypes = ["cover", "frontmatter", "titlepage", "halftitlepage", "copyright-page",
        "dedication", "epigraph", "foreword", "preface", "toc", "imprint", "impressum", "colophon"];

    public async Task<IReadOnlyList<ArtifactExcerpt>> ExtractAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (asset.Format.TrimStart('.').ToUpperInvariant() is not ("EPUB" or "KEPUB")) return [];
        try
        {
            await using var content = await staging.OpenAsync(asset.StorageState, asset.StoredFilename, cancellationToken);
            // ZipArchive otherwise buffers an entire non-seekable source without an application bound.
            if (!content.CanSeek) return [];
            using var zip = new ZipArchive(content, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > 10_000) return [];
            long declared = 0;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                declared += entry.Length;
                if (!paths.Add(entry.FullName) || UnsafePath(entry.FullName) || entry.Length > 200L * 1024 * 1024 ||
                    declared > 2L * 1024 * 1024 * 1024 || entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 100) return [];
            }
            long read = 0;
            async Task<XDocument> Load(string path, long cap)
            {
                var entry = zip.GetEntry(path);
                if (entry is null || entry.Length > cap || read + entry.Length > EpubProfileInspector.MaxTotalBytes)
                    throw new InvalidDataException("EPUB excerpt read limit.");
                await using var source = entry.Open();
                using var bytes = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    read += count;
                    if (bytes.Length + count > cap || read > EpubProfileInspector.MaxTotalBytes)
                        throw new InvalidDataException("EPUB excerpt read limit.");
                    bytes.Write(buffer, 0, count);
                }
                bytes.Position = 0;
                using var reader = XmlReader.Create(bytes, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = cap });
                var document = XDocument.Load(reader, LoadOptions.None);
                cancellationToken.ThrowIfCancellationRequested();
                return document;
            }
            var container = await Load("META-INF/container.xml", 1024 * 1024);
            var root = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(root) || UnsafePath(root)) return [];
            var package = await Load(root, 1024 * 1024);
            var directory = root.Contains('/', StringComparison.Ordinal) ? root[..root.LastIndexOf('/')] : "";
            var manifest = package.Descendants().Where(e => e.Name.LocalName == "item")
                .ToDictionary(e => e.Attribute("id")?.Value ?? "", e => e, StringComparer.Ordinal);
            var spine = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine")?.Elements()
                .Where(e => e.Name.LocalName == "itemref").ToArray() ?? [];
            if (spine.Length is 0 or > 10_000) return [];
            var lastLinear = Array.FindLastIndex(spine, e => e.Attribute("linear")?.Value != "no");
            var result = new List<ArtifactExcerpt>();
            for (var index = 0; index < spine.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemref = spine[index];
                var linear = itemref.Attribute("linear")?.Value != "no";
                var copyrightWindow = index < 5 || index >= spine.Length - 3;
                if ((!copyrightWindow || result.Any(e => e.Id == "tc-copyright")) &&
                    (!linear || result.Any(e => e.Id == "tc-chapter1")) && index != lastLinear) continue;
                if (!manifest.TryGetValue(itemref.Attribute("idref")?.Value ?? "", out var item)) return [];
                var path = EpubValidator.ResolveArchivePath(directory, item.Attribute("href")?.Value ?? "");
                if (path is null || UnsafePath(path)) return [];
                var document = await Load(path, EpubProfileInspector.MaxItemBytes);
                var body = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? document.Root;
                if (body is null) return [];
                var types = body.DescendantsAndSelf().Attributes(XName.Get("type", Ops)).SelectMany(a => a.Value.Split(' ')).ToArray();
                var heading = body.Descendants().FirstOrDefault(e => e.Name.LocalName is "h1" or "h2" or "h3")?.Value ?? "";
                var copyright = Copyright(Path.GetFileName(path)) || types.Any(Copyright) || Copyright(heading);
                var text = Clean(string.Join(' ', body.DescendantNodes().OfType<XText>()
                    .Where(n => !n.Ancestors().Any(e => e.Name.LocalName is "script" or "style" or "head" or "nav"))
                    .Select(n => n.Value)));
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                void Add(string id, string value, bool tail = false)
                {
                    if (value.Length > 1200) value = tail ? value[^1200..] : value[..1200];
                    if (value.Length > 0 && char.IsLowSurrogate(value[0])) value = value[1..];
                    if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
                    if (value.Length > 0) result.Add(new(id, value));
                }
                if (copyrightWindow && copyright && !result.Any(e => e.Id == "tc-copyright")) Add("tc-copyright", text);
                var front = copyright || types.Any(t => FrontTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                    Regex.IsMatch(Path.GetFileNameWithoutExtension(path) + " " + heading,
                        @"\b(cover|titlepage|dedication|foreword|preface|contents)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
                if (linear && !front && words.Length >= 200 && !result.Any(e => e.Id == "tc-chapter1"))
                    Add("tc-chapter1", string.Join(' ', words.Take(200)));
                if (index == lastLinear) Add("tc-last", string.Join(' ', words.TakeLast(150)), true);
            }
            return result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No exception detail from a book is allowed into diagnostics or persistence.
            return [];
        }
    }

    private static bool Copyright(string value) => Regex.IsMatch(value, "copyright|imprint|impressum|colophon",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);

    private static bool UnsafePath(string path) => path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
        path.Split('/').Any(p => p is "." or "..");

    private static string Clean(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var safe = new string(normalized.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format &&
            c is not ('<' or '>' or '\u034f') && c is not (>= '\ufe00' and <= '\ufe0f') && (!char.IsControl(c) || char.IsWhiteSpace(c))).ToArray());
        safe = safe.Replace("BOOK_TEXT", "BOOK TEXT", StringComparison.OrdinalIgnoreCase);
        return string.Join(' ', safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
