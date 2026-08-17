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
        if (!string.Equals(asset.Format, ".epub", StringComparison.OrdinalIgnoreCase))
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

            return new ValidationOutcome(true, null);
        }
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
