using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Structural validation for the EPUB archive format — <c>EpubValidator</c>
/// from <c>docs/03-provider-api-contracts.md</c> §8, and the concrete answer
/// to F1's "zip-bomb and path-traversal protection for archive-based formats
/// (EPUB)" requirement from <c>docs/01</c> §13.1.
/// </summary>
/// <remarks>
/// Nothing here ever extracts an entry to the filesystem — every check reads
/// either central-directory metadata (entry count, declared compressed/
/// uncompressed sizes: available without decompressing anything) or a
/// specific named entry's bytes into memory, bounded and capped. That in-
/// memory, metadata-first approach is what stands in for "sandboxed
/// extraction" here: there is no extraction step to sandbox, and per
/// Microsoft's own guidance neither <see cref="ZipArchive"/> nor its
/// convenience methods enforce any of these limits on their own — see
/// https://learn.microsoft.com/dotnet/standard/io/zip-tar-best-practices.
/// <para>
/// Confirming the mandatory first "mimetype" entry is <see cref="FileTypeValidator"/>'s
/// job, not this validator's: that check depends on physical byte order in
/// the archive (the Open Container Format requires "mimetype" to be the
/// literal first local file record), which <see cref="ZipArchive.Entries"/>'s
/// central-directory-derived ordering does not reliably preserve.
/// <see cref="Infrastructure.Acquisition.SignatureFileTypeDetector"/> already
/// reads that from the true byte offset. This validator instead owns
/// reference integrity (does <c>META-INF/container.xml</c> point at an OPF
/// file that actually exists) and the safety limits below.
/// </para>
/// </remarks>
public sealed class EpubValidator : IAssetValidator
{
    private const long MaxEntryUncompressedBytes = 200L * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxMetadataEntryBytes = 1024 * 1024;
    private const int MaxEntryCount = 10_000;

    /// <summary>
    /// Generous relative to legitimate EPUB content — markup and text rarely
    /// compress past ~20:1 — but well under DEFLATE's ~1032:1 theoretical
    /// ceiling, so a genuine flat zip bomb is still caught with real margin.
    /// </summary>
    private const double MaxCompressionRatio = 100.0;

    public string Id => "epub-structure";

    public async Task<ValidationOutcome> ValidateAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        if (!string.Equals(asset.Format, ".epub", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(asset.Format, ".kepub", StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationOutcome(true, null);
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return new ValidationOutcome(false, "The file is not a valid ZIP archive.");
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntryCount)
            {
                return new ValidationOutcome(
                    false, $"The archive has more than {MaxEntryCount} entries.");
            }

            var totalUncompressedBytes = 0L;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsPathUnsafe(entry.FullName))
                {
                    return new ValidationOutcome(
                        false, $"The archive entry '{entry.FullName}' has an unsafe path.");
                }

                if (entry.Length > MaxEntryUncompressedBytes)
                {
                    return new ValidationOutcome(
                        false, $"The archive entry '{entry.FullName}' is larger than the allowed limit.");
                }

                totalUncompressedBytes += entry.Length;
                if (totalUncompressedBytes > MaxTotalUncompressedBytes)
                {
                    return new ValidationOutcome(
                        false, "The archive's total uncompressed size exceeds the allowed limit.");
                }

                // A directory entry, or an entry stored with no compression,
                // both legitimately have CompressedLength == 0 or == Length;
                // only check the ratio once there is a real compressed size
                // to divide by.
                if (entry.CompressedLength > 0 &&
                    entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
                {
                    return new ValidationOutcome(
                        false, $"The archive entry '{entry.FullName}' has an implausible compression ratio.");
                }
            }

            var containerEntry = archive.GetEntry("META-INF/container.xml");
            if (containerEntry is null)
            {
                return new ValidationOutcome(false, "The archive has no META-INF/container.xml.");
            }

            if (containerEntry.Length > MaxMetadataEntryBytes)
            {
                return new ValidationOutcome(false, "META-INF/container.xml is larger than expected.");
            }

            string rootfilePath;
            try
            {
                rootfilePath = await ReadRootfilePathAsync(containerEntry, cancellationToken);
            }
            catch (Exception exception) when (exception is XmlException or InvalidOperationException)
            {
                return new ValidationOutcome(false, "META-INF/container.xml is not valid XML.");
            }

            if (string.IsNullOrWhiteSpace(rootfilePath))
            {
                return new ValidationOutcome(
                    false, "META-INF/container.xml has no rootfile full-path.");
            }

            if (IsPathUnsafe(rootfilePath) || archive.GetEntry(rootfilePath) is null)
            {
                return new ValidationOutcome(
                    false, $"container.xml references '{rootfilePath}', which is not in the archive.");
            }

            // E03: a package whose reading order lists nothing readable is not a usable book, whatever
            // its metadata says. Deliberately narrow: only a package we could parse and whose spine is
            // missing, empty, or lists no content document present in the archive is rejected. A package
            // we cannot parse is left to the identity verifier exactly as before, and nothing here
            // looks at how much content there is (no minimum chapter count or length).
            if (await HasUsableSpineAsync(archive, rootfilePath, cancellationToken) == false)
            {
                return new ValidationOutcome(false, NoReadingOrderMessage);
            }

            return new ValidationOutcome(true, null);
        }
    }

    internal const string NoReadingOrderMessage =
        "The EPUB has no usable reading order: its spine is missing or empty, or lists no content document that is present in the archive.";

    /// <summary>
    /// <see langword="true"/> when at least one spine itemref resolves, through the manifest, to a file that
    /// exists in the archive; <see langword="false"/> when the package parsed but none does;
    /// <see langword="null"/> when the package file could not be read here (too large, or not XML).
    /// </summary>
    private static async Task<bool?> HasUsableSpineAsync(
        ZipArchive archive, string rootfilePath, CancellationToken cancellationToken)
    {
        var package = archive.GetEntry(rootfilePath);
        if (package is null || package.Length > MaxMetadataEntryBytes)
        {
            return null;
        }

        XDocument document;
        try
        {
            await using var entryStream = package.Open();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true };
            using var xmlReader = XmlReader.Create(entryStream, settings);
            document = await XDocument.LoadAsync(xmlReader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException)
        {
            return null;
        }

        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "item"))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(href))
            {
                manifest.TryAdd(id, href);
            }
        }

        var directory = rootfilePath.Contains('/', StringComparison.Ordinal)
            ? rootfilePath[..rootfilePath.LastIndexOf('/')]
            : string.Empty;
        foreach (var itemref in document.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // linear="no" items are still reading-order items; only presence matters here.
            var idref = itemref.Attribute("idref")?.Value;
            if (idref is not null && manifest.TryGetValue(idref, out var href) &&
                ResolveArchivePath(directory, href) is { } path && archive.GetEntry(path) is { Name.Length: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves a manifest href against the package directory, or <see langword="null"/> when it does not name a local archive file.</summary>
    private static string? ResolveArchivePath(string packageDirectory, string href)
    {
        var reference = href.Split('#', '?')[0];
        if (reference.Length == 0 || reference.Contains("://", StringComparison.Ordinal) || reference.StartsWith('/'))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(reference);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var segments = new List<string>(packageDirectory.Length == 0 ? [] : packageDirectory.Split('/'));
        foreach (var segment in decoded.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        var path = string.Join('/', segments);
        return path.Length == 0 || IsPathUnsafe(path) ? null : path;
    }

    /// <summary>
    /// A synthetic anchor resolved through <see cref="Path.GetFullPath(string)"/>
    /// itself, not written as a literal — a hand-written Unix-style
    /// <c>"/anchor/"</c> string never matches what <see cref="Path.GetFullPath(string)"/>
    /// actually returns on Windows, where a leading <c>/</c> resolves against
    /// the current drive rather than being treated as already absolute. Both
    /// sides of the comparison in <see cref="IsPathUnsafe"/> have to go
    /// through the same normalization or every entry looks unsafe.
    /// </summary>
    private static readonly string PathTraversalAnchor =
        Path.GetFullPath("epub-validator-anchor") + Path.DirectorySeparatorChar;

    /// <summary>
    /// Resolves the entry name against <see cref="PathTraversalAnchor"/> and
    /// confirms the result never leaves it — the same technique Microsoft's
    /// own path-traversal guidance for <see cref="ZipArchiveEntry"/> uses,
    /// even though nothing here is actually extracted to that anchor.
    /// </summary>
    private static bool IsPathUnsafe(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath) || Path.IsPathRooted(entryPath))
        {
            return true;
        }

        var resolved = Path.GetFullPath(Path.Combine(PathTraversalAnchor, entryPath));
        return !resolved.StartsWith(PathTraversalAnchor, StringComparison.Ordinal);
    }

    private static async Task<string> ReadRootfilePathAsync(
        ZipArchiveEntry containerEntry, CancellationToken cancellationToken)
    {
        await using var entryStream = containerEntry.Open();

        // DTD processing is explicitly prohibited and the resolver is null,
        // so this neither expands entities nor fetches any external
        // reference — a crafted container.xml cannot use this parse to read
        // host files or make the host issue outbound requests.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            Async = true
        };

        using var xmlReader = XmlReader.Create(entryStream, settings);
        var document = await XDocument.LoadAsync(xmlReader, LoadOptions.None, cancellationToken);

        var rootfile = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile");

        return rootfile?.Attribute("full-path")?.Value?.Trim() ?? string.Empty;
    }
}
