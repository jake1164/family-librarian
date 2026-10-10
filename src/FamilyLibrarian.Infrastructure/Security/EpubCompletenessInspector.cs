using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Collects deterministic, bounded completeness <em>evidence</em> from an EPUB package (alpha.6 M1-3b).
/// </summary>
/// <remarks>
/// Evidence only: it never decides anything, never changes an identity verdict, and applies no minimum
/// chapter count or length (a short novella is a legitimate book). It reports measurable facts and
/// explicit cues that the file is a sample or was truncated:
/// <list type="bullet">
/// <item>an explicit sample/excerpt/preview/teaser marker in the package's title, subject or type
/// metadata, unless that word is part of the requested work's own title;</item>
/// <item>navigation (table of contents) links whose target file is not in the archive;</item>
/// <item>reading-order items whose manifest target file is not in the archive;</item>
/// <item>the same file listed more than once in the reading order.</item>
/// </list>
/// The absence of every cue is <em>not</em> proof of completeness and is never recorded as such. A
/// sample with clean metadata and an intact structure is not detectable here; that needs a semantic
/// assessment or a human. Only counts and short marker words are reported; no book text is kept.
/// </remarks>
internal static class EpubCompletenessInspector
{
    private const long MaxNavigationBytes = 1024 * 1024;
    private const int MaxLinksCounted = 10_000;
    private const int MaxMetadataFieldLength = 500;

    private static readonly string[] SampleMarkers = ["sample", "excerpt", "preview", "teaser"];

    public static IReadOnlyList<IdentityFact> Inspect(
        ZipArchive archive, string rootfilePath, IReadOnlyCollection<string> requestedTitles)
    {
        try
        {
            return InspectCore(archive, rootfilePath, requestedTitles);
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException or
                                              UriFormatException or ArgumentException or InvalidOperationException)
        {
            // Evidence is best effort and must never change the verification outcome.
            return [];
        }
    }

    private static List<IdentityFact> InspectCore(
        ZipArchive archive, string rootfilePath, IReadOnlyCollection<string> requestedTitles)
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
                manifest.TryAdd(id, (href, item.Attribute("properties")?.Value ?? string.Empty, item.Attribute("media-type")?.Value ?? string.Empty));
            }
        }

        var findings = new List<IdentityFact>();

        // Reading order measured against the archive.
        var spineTargets = new List<string?>();
        foreach (var itemref in package.Descendants().Where(element => element.Name.LocalName == "itemref").Take(MaxLinksCounted))
        {
            var idref = itemref.Attribute("idref")?.Value;
            spineTargets.Add(idref is not null && manifest.TryGetValue(idref, out var entry)
                ? EpubValidator.ResolveArchivePath(directory, entry.Href)
                : null);
        }

        var spinePresent = spineTargets.Where(path => path is not null && archive.GetEntry(path) is { Name.Length: > 0 }).Select(path => path!).ToArray();
        var spineMissing = spineTargets.Count - spinePresent.Length;
        var spineDuplicates = spinePresent.Length - spinePresent.Distinct(StringComparer.Ordinal).Count();

        // Navigation (table of contents) links measured against the archive.
        var (navLinks, navMissing) = CountNavigation(archive, package, manifest, directory);

        findings.Add(new("ev-completeness-counts", EvidenceDimension.Completeness,
            EvidenceReasonCodes.ArtifactCompleteness.ReadingOrderCounted, EvidencePolarity.Unknown,
            $"spine={spineTargets.Count};spineResolved={spinePresent.Length};navLinks={navLinks}"));

        var marker = FindSampleMarker(package, requestedTitles);
        if (marker is not null)
        {
            findings.Add(new("ev-completeness-sample-marker", EvidenceDimension.Completeness,
                EvidenceReasonCodes.ArtifactCompleteness.SampleMarkerInMetadata, EvidencePolarity.Condition, marker));
        }

        if (navMissing > 0)
        {
            findings.Add(new("ev-completeness-toc-missing", EvidenceDimension.Completeness,
                EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Condition, $"{navMissing}/{navLinks}"));
        }

        if (spineMissing > 0)
        {
            findings.Add(new("ev-completeness-spine-missing", EvidenceDimension.Completeness,
                EvidenceReasonCodes.ArtifactCompleteness.SpineContentMissing, EvidencePolarity.Condition,
                $"{spineMissing}/{spineTargets.Count}"));
        }

        if (spineDuplicates > 0)
        {
            findings.Add(new("ev-completeness-spine-duplicates", EvidenceDimension.Completeness,
                EvidenceReasonCodes.ArtifactCompleteness.DuplicateReadingOrderItems, EvidencePolarity.Condition,
                spineDuplicates.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (findings.Count == 1)
        {
            // Explicitly Unknown: checking for cues and finding none does not show the book is complete.
            findings.Add(new("ev-completeness-no-cue", EvidenceDimension.Completeness,
                EvidenceReasonCodes.ArtifactCompleteness.NoCueDetected, EvidencePolarity.Unknown,
                "absence of cues is not proof of completeness"));
        }

        return findings;
    }

    private static (int Links, int Missing) CountNavigation(
        ZipArchive archive, XDocument package,
        Dictionary<string, (string Href, string Properties, string MediaType)> manifest, string packageDirectory)
    {
        var links = 0;
        var missing = 0;
        foreach (var navItem in manifest.Values.Where(item =>
                     item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.Ordinal) ||
                     item.MediaType == "application/x-dtbncx+xml"))
        {
            var navPath = EpubValidator.ResolveArchivePath(packageDirectory, navItem.Href);
            if (navPath is null || Load(archive.GetEntry(navPath)) is not { } navigation) continue;

            var navDirectory = DirectoryOf(navPath);
            var targets = navItem.MediaType == "application/x-dtbncx+xml"
                ? navigation.Descendants().Where(element => element.Name.LocalName == "content").Select(element => element.Attribute("src")?.Value)
                : navigation.Descendants().Where(element => element.Name.LocalName == "a").Select(element => element.Attribute("href")?.Value);

            foreach (var target in targets.Where(value => !string.IsNullOrWhiteSpace(value)).Take(MaxLinksCounted - links))
            {
                // A bare "#fragment" points inside the same document and says nothing about other files.
                if (target!.StartsWith('#')) continue;
                links++;
                var path = EpubValidator.ResolveArchivePath(navDirectory, target);
                if (path is null || archive.GetEntry(path) is not { Name.Length: > 0 }) missing++;
            }
        }

        return (links, missing);
    }

    /// <summary>The first sample-style marker found in title, subject or type metadata, as <c>field:word</c>.</summary>
    private static string? FindSampleMarker(XDocument package, IReadOnlyCollection<string> requestedTitles)
    {
        var requestedWords = requestedTitles
            .SelectMany(title => Words(title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var field in new[] { "title", "subject", "type" })
        {
            foreach (var element in package.Descendants().Where(candidate => candidate.Name.LocalName == field &&
                         candidate.Name.NamespaceName.Contains("purl.org/dc", StringComparison.Ordinal)))
            {
                var text = element.Value.Length > MaxMetadataFieldLength ? element.Value[..MaxMetadataFieldLength] : element.Value;
                foreach (var word in Words(text))
                {
                    // A marker word that belongs to the requested work's own title is part of its identity.
                    if (SampleMarkers.Contains(word, StringComparer.OrdinalIgnoreCase) && !requestedWords.Contains(word))
                        return $"{field}:{word.ToLowerInvariant()}";
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Matches(text, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(match => match.Value);

    private static XDocument? Load(ZipArchiveEntry? entry)
    {
        if (entry is null || entry.Length > MaxNavigationBytes) return null;
        using var stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 5_000_000 };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string DirectoryOf(string path) =>
        path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
}
