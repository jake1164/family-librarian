using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Describes an EPUB for the artifact profile (AUTOFIND 3c): package metadata (tier A) and the book's structure text
/// (tier B). Every finding has Unknown polarity: the profile describes the file and judges nothing; a later, separate
/// comparison weighs it against what was expected.
/// </summary>
/// <remarks>
/// <para><b>Tier A.</b> Publisher, date, ISBN-like identifiers (at most 5), language, series name and position, and a word
/// count summed over the reading order.</para>
/// <para><b>Tier B</b> (structure, owner decision AF-3). The first 40 table-of-contents entry titles, the first 40 h1/h2
/// heading titles, and at most 10 front-matter and 10 back-matter section names. Titles are bounded and stripped of control
/// characters. No running text is ever kept: words are counted and discarded.</para>
/// <para>Bounded like the other EPUB readers: 64 MiB of decompressed XHTML in total, 8 MiB per reading-order item, DTDs
/// prohibited and no external resolver. A bound that is hit, an unreadable item, or a CJK or Thai package language (where
/// "word" has no usable definition) records <c>word_count_unavailable</c> with the reason instead of a guess. Best effort:
/// nothing here can throw or change a verdict.</para>
/// </remarks>
internal static class EpubProfileInspector
{
    internal const long MaxTotalBytes = 64L * 1024 * 1024;
    internal const long MaxItemBytes = 8L * 1024 * 1024;
    internal const int MaxTocEntries = 40;
    internal const int MaxHeadings = 40;
    internal const int MaxSections = 10;
    internal const int MaxIdentifiers = 5;

    private const long MaxMetadataBytes = 1024 * 1024;
    private const int MaxSpineItems = 10_000;
    private const int MaxTitleLength = 256;
    private const string OpsNamespace = "http://www.idpf.org/2007/ops";

    private static readonly string[] UncountableLanguages = ["zh", "ja", "ko", "th"];

    private static readonly string[] FrontMatterTypes =
        ["cover", "frontmatter", "titlepage", "halftitlepage", "copyright-page", "dedication", "epigraph", "foreword", "preface"];

    private static readonly string[] BackMatterTypes =
        ["backmatter", "appendix", "afterword", "acknowledgments", "glossary", "bibliography", "index", "colophon", "endnotes",
            "footnotes", "rearnotes"];

    public static IReadOnlyList<IdentityFact> Inspect(ZipArchive archive, string rootfilePath)
    {
        try
        {
            return InspectCore(archive, rootfilePath);
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException or
                                              UriFormatException or ArgumentException or InvalidOperationException or
                                              RegexMatchTimeoutException)
        {
            // Evidence is best effort and must never change the verification outcome.
            return [];
        }
    }

    private static List<IdentityFact> InspectCore(ZipArchive archive, string rootfilePath)
    {
        var package = Load(archive.GetEntry(rootfilePath));
        if (package is null) return [];

        var directory = DirectoryOf(rootfilePath);
        var manifest = new Dictionary<string, (string Href, string Properties, string MediaType)>(StringComparer.Ordinal);
        foreach (var item in package.Descendants().Where(element => element.Name.LocalName == "item"))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(href))
            {
                manifest.TryAdd(id, (href, item.Attribute("properties")?.Value ?? string.Empty,
                    item.Attribute("media-type")?.Value ?? string.Empty));
            }
        }

        var facts = new List<IdentityFact>();
        var language = AddPackageMetadata(package, facts);
        AddSeries(package, facts);

        var spine = new List<string>();
        foreach (var itemref in package.Descendants().Where(element => element.Name.LocalName == "itemref").Take(MaxSpineItems))
        {
            var idref = itemref.Attribute("idref")?.Value;
            if (idref is not null && manifest.TryGetValue(idref, out var entry) &&
                EpubValidator.ResolveArchivePath(directory, entry.Href) is { } path && !spine.Contains(path, StringComparer.Ordinal))
            {
                spine.Add(path);
            }
        }

        var walk = Walk(archive, spine, language);
        facts.Add(walk.WordCount);
        AddToc(archive, manifest, directory, facts);
        AddIndexed(facts, walk.Headings, "ev-heading", EvidenceReasonCodes.ArtifactProfile.Heading);
        AddIndexed(facts, walk.FrontMatter, "ev-front", EvidenceReasonCodes.ArtifactProfile.FrontMatter);
        AddIndexed(facts, walk.BackMatter, "ev-back", EvidenceReasonCodes.ArtifactProfile.BackMatter);
        return facts;
    }

    /// <summary>Publisher, date, ISBN-like identifiers and language. Returns the package language for the word-count rule.</summary>
    private static string? AddPackageMetadata(XDocument package, List<IdentityFact> facts)
    {
        string? FirstDc(string name) => package.Descendants()
            .Where(element => element.Name.LocalName == name && element.Name.NamespaceName.Contains("purl.org/dc", StringComparison.Ordinal))
            .Select(element => Clean(element.Value))
            .FirstOrDefault(value => value is not null);

        AddIfPresent(facts, "ev-profile-publisher", EvidenceReasonCodes.ArtifactProfile.PackagePublisher, FirstDc("publisher"));
        AddIfPresent(facts, "ev-profile-date", EvidenceReasonCodes.ArtifactProfile.PackageDate, FirstDc("date"));
        foreach (var field in package.Descendants().Where(e => e.Name.LocalName is "title" or "description" or "subject" &&
                     e.Name.NamespaceName.Contains("purl.org/dc", StringComparison.Ordinal)).Take(20))
        {
            if (ArtifactProfileComparer.FindAbridgedMarker(field.Value) is { } marker)
            {
                facts.Add(new IdentityFact("ev-package-abridged", EvidenceDimension.Profile,
                    EvidenceReasonCodes.ProfileComparison.AbridgedMarker, EvidencePolarity.Condition, marker));
                break;
            }
        }

        var identifiers = package.Descendants()
            .Where(element => element.Name.LocalName == "identifier" && element.Name.NamespaceName.Contains("purl.org/dc", StringComparison.Ordinal))
            .Select(element => IsbnOf(element.Value))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaxIdentifiers)
            .ToArray();
        for (var index = 0; index < identifiers.Length; index++)
        {
            facts.Add(Fact($"ev-profile-identifier-{index + 1}", EvidenceReasonCodes.ArtifactProfile.PackageIdentifier, identifiers[index]));
        }

        var language = FirstDc("language");
        AddIfPresent(facts, "ev-profile-language", EvidenceReasonCodes.ArtifactProfile.PackageLanguage, language);
        return language;
    }

    /// <summary>Calibre's series meta, or the EPUB 3 collection meta refined by a group position.</summary>
    private static void AddSeries(XDocument package, List<IdentityFact> facts)
    {
        var metas = package.Descendants().Where(element => element.Name.LocalName == "meta").ToArray();
        var name = Clean(metas.FirstOrDefault(meta => meta.Attribute("name")?.Value == "calibre:series")?.Attribute("content")?.Value);
        var index = Clean(metas.FirstOrDefault(meta => meta.Attribute("name")?.Value == "calibre:series_index")?.Attribute("content")?.Value);

        if (name is null)
        {
            var collection = metas.FirstOrDefault(meta => meta.Attribute("property")?.Value == "belongs-to-collection" &&
                meta.Attribute("id")?.Value is { Length: > 0 });
            if (collection is not null)
            {
                var reference = "#" + collection.Attribute("id")!.Value;
                var refinements = metas.Where(meta => meta.Attribute("refines")?.Value == reference).ToArray();
                var type = refinements.FirstOrDefault(meta => meta.Attribute("property")?.Value == "collection-type")?.Value.Trim();
                if (type is null or "series")
                {
                    name = Clean(collection.Value);
                    index = Clean(refinements.FirstOrDefault(meta => meta.Attribute("property")?.Value == "group-position")?.Value);
                }
            }
        }

        if (name is not null)
        {
            facts.Add(Fact("ev-profile-series", EvidenceReasonCodes.ArtifactProfile.PackageSeries,
                index is null ? name : $"{name}#{index}"));
        }
    }

    private sealed record Walked(
        IdentityFact WordCount, List<string> Headings, List<string> FrontMatter, List<string> BackMatter);

    /// <summary>
    /// One streaming pass over the reading order: counts words (discarding the text), captures h1/h2 titles, and collects
    /// <c>epub:type</c> section names.
    /// </summary>
    private static Walked Walk(ZipArchive archive, IReadOnlyList<string> spine, string? packageLanguage)
    {
        var headings = new List<string>();
        var front = new List<string>();
        var back = new List<string>();
        var countable = !IsUncountableLanguage(packageLanguage);
        string? unavailable = countable ? null : "language";
        long words = 0, totalBytes = 0;
        var items = 0;

        foreach (var path in spine)
        {
            if (archive.GetEntry(path) is not { Name.Length: > 0 } entry) continue;
            if (entry.Length > MaxItemBytes || totalBytes + entry.Length > MaxTotalBytes)
            {
                unavailable ??= "cap";
                break;
            }

            try
            {
                using var raw = entry.Open();
                var limited = new LimitedReadStream(raw, MaxItemBytes);
                var itemWords = ReadItem(limited, countable && unavailable is null, headings, front, back);
                totalBytes += limited.BytesRead;
                if (countable && unavailable is null)
                {
                    words += itemWords;
                    items++;
                }
            }
            catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException)
            {
                // A DTD, a malformed item, or a stream longer than its header claimed: no honest count exists.
                unavailable ??= exception is InvalidDataException ? "cap" : "unreadable";
            }
        }

        var wordCount = unavailable is not null
            ? Fact("ev-profile-words", EvidenceReasonCodes.ArtifactProfile.TextWordCountUnavailable, $"reason={unavailable}")
            : items == 0
                ? Fact("ev-profile-words", EvidenceReasonCodes.ArtifactProfile.TextWordCountUnavailable, "reason=no_reading_order")
                : Fact("ev-profile-words", EvidenceReasonCodes.ArtifactProfile.TextWordCount,
                    $"words={words.ToString(CultureInfo.InvariantCulture)};items={items.ToString(CultureInfo.InvariantCulture)};basis=xhtml_text");
        return new Walked(wordCount, headings, front, back);
    }

    private static long ReadItem(
        Stream content, bool count, List<string> headings, List<string> front, List<string> back)
    {
        using var reader = XmlReader.Create(content, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxItemBytes
        });

        long words = 0;
        var inWord = false;
        var skipDepth = -1;
        var headingDepth = -1;
        StringBuilder? heading = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    CollectSectionTypes(reader, front, back);
                    if (skipDepth < 0 && reader.LocalName is "script" or "style")
                    {
                        skipDepth = reader.Depth;
                    }
                    else if (skipDepth < 0 && headingDepth < 0 && headings.Count < MaxHeadings && reader.LocalName is "h1" or "h2")
                    {
                        (headingDepth, heading) = (reader.Depth, new StringBuilder());
                    }

                    inWord = false;
                    if (reader.IsEmptyElement) EndElement(reader.Depth, reader.LocalName);
                    break;
                case XmlNodeType.EndElement:
                    EndElement(reader.Depth, reader.LocalName);
                    inWord = false;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    if (skipDepth >= 0) break;
                    var text = reader.Value;
                    if (count) words += CountWords(text, ref inWord);
                    if (heading is not null && heading.Length < MaxTitleLength * 2) heading.Append(text);
                    break;
            }
        }

        return words;

        void EndElement(int depth, string localName)
        {
            if (skipDepth == depth && localName is "script" or "style") skipDepth = -1;
            if (headingDepth == depth && heading is not null)
            {
                if (Clean(heading.ToString()) is { } title) headings.Add(title);
                (headingDepth, heading) = (-1, null);
            }
        }
    }

    private static void CollectSectionTypes(XmlReader reader, List<string> front, List<string> back)
    {
        if (reader.GetAttribute("type", OpsNamespace) is not { Length: > 0 } value) return;
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (FrontMatterTypes.Contains(token, StringComparer.Ordinal) && front.Count < MaxSections && !front.Contains(token))
                front.Add(token);
            else if (BackMatterTypes.Contains(token, StringComparer.Ordinal) && back.Count < MaxSections && !back.Contains(token))
                back.Add(token);
        }
    }

    /// <summary>Words are maximal runs of letters or digits; a run split across two text nodes is one word.</summary>
    private static long CountWords(string text, ref bool inWord)
    {
        long words = 0;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (!inWord) words++;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return words;
    }

    private static void AddToc(
        ZipArchive archive, Dictionary<string, (string Href, string Properties, string MediaType)> manifest,
        string packageDirectory, List<IdentityFact> facts)
    {
        var titles = new List<string>();
        foreach (var item in manifest.Values.Where(item =>
                     item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.Ordinal) ||
                     item.MediaType == "application/x-dtbncx+xml"))
        {
            if (titles.Count >= MaxTocEntries) break;
            if (EpubValidator.ResolveArchivePath(packageDirectory, item.Href) is not { } path || Load(archive.GetEntry(path)) is not { } document)
                continue;

            IEnumerable<string> entries;
            if (item.MediaType == "application/x-dtbncx+xml")
            {
                entries = document.Descendants().Where(element => element.Name.LocalName == "navLabel")
                    .Select(label => label.Elements().FirstOrDefault(child => child.Name.LocalName == "text")?.Value ?? label.Value);
            }
            else
            {
                var navs = document.Descendants().Where(element => element.Name.LocalName == "nav").ToArray();
                var toc = navs.FirstOrDefault(nav => nav.Attributes().Any(attribute =>
                              attribute.Name.LocalName == "type" && attribute.Value.Split(' ').Contains("toc", StringComparer.Ordinal)))
                          ?? navs.FirstOrDefault();
                entries = toc is null
                    ? []
                    : toc.Descendants().Where(element => element.Name.LocalName == "a").Select(anchor => anchor.Value);
            }

            foreach (var title in entries.Select(Clean).OfType<string>())
            {
                if (titles.Count >= MaxTocEntries) break;
                titles.Add(title);
            }
        }

        AddIndexed(facts, titles, "ev-toc", EvidenceReasonCodes.ArtifactProfile.TocEntry);
    }

    private static void AddIndexed(List<IdentityFact> facts, List<string> values, string idPrefix, string code)
    {
        for (var index = 0; index < values.Count; index++)
        {
            facts.Add(Fact($"{idPrefix}-{index + 1}", code, values[index]));
        }
    }

    private static void AddIfPresent(List<IdentityFact> facts, string id, string code, string? value)
    {
        if (value is not null) facts.Add(Fact(id, code, value));
    }

    private static IdentityFact Fact(string id, string code, string value) =>
        new(id, EvidenceDimension.Profile, code, EvidencePolarity.Unknown, value);

    /// <summary>Strips control characters, collapses whitespace and bounds the length; null when nothing is left.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var collapsed = string.Join(' ', ArtifactEvidence.Bound(value, MaxTitleLength * 4)?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? []);
        return ArtifactEvidence.Bound(collapsed, MaxTitleLength);
    }

    /// <summary>An ISBN-10 or ISBN-13 with optional hyphens and an optional <c>urn:isbn:</c> prefix; anything else is not stored.</summary>
    private static string? IsbnOf(string value)
    {
        var candidate = value.Trim();
        if (candidate.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)) candidate = candidate["urn:isbn:".Length..];
        candidate = candidate.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return candidate.Length == 13 && candidate.All(char.IsAsciiDigit) ||
               candidate.Length == 10 && candidate[..9].All(char.IsAsciiDigit) && (char.IsAsciiDigit(candidate[9]) || candidate[9] is 'X' or 'x')
            ? candidate.ToUpperInvariant()
            : null;
    }

    private static bool IsUncountableLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var primary = language.Trim().Split('-', '_')[0];
        return UncountableLanguages.Contains(primary, StringComparer.OrdinalIgnoreCase) ||
               primary.Equals("zho", StringComparison.OrdinalIgnoreCase) || primary.Equals("jpn", StringComparison.OrdinalIgnoreCase) ||
               primary.Equals("kor", StringComparison.OrdinalIgnoreCase) || primary.Equals("tha", StringComparison.OrdinalIgnoreCase) ||
               primary.Equals("chi", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument? Load(ZipArchiveEntry? entry)
    {
        if (entry is null || entry.Length > MaxMetadataBytes) return null;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 5_000_000
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string DirectoryOf(string path) =>
        path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    /// <summary>Counts the bytes actually read and refuses to read more than its limit, whatever the ZIP header claimed.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            if (BytesRead > limit) throw new InvalidDataException("An archive entry is larger than its header declared or the limit allows.");
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
