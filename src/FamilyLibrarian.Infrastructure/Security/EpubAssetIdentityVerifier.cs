using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Reads an EPUB package's Dublin Core title and creator without extracting any
/// file, then requires both to match the Work being fulfilled.
/// </summary>
/// <remarks>
/// This is deliberately conservative: missing, malformed, or slightly
/// different metadata is held as unmatched rather than guessed into a request.
/// ZIP entry sizes and XML parsing are bounded independently of
/// <see cref="EpubValidator"/>, because an identity verifier handles the same
/// untrusted input and must remain safe if its invocation order changes.
/// </remarks>
public sealed class EpubAssetIdentityVerifier(
    IWorkLookup works, IBookMatcher bookMatcher, IBookRequestFulfillmentStore requestFulfillment) : IAssetIdentityVerifier
{
    private const long MaxMetadataEntryBytes = 1024 * 1024;
    private const int MaxEntryCount = 10_000;

    public string Id => "epub-package-metadata";

    public bool Supports(MediaAsset asset) =>
        string.Equals(asset.Format, ".epub", StringComparison.OrdinalIgnoreCase);

    public async Task<AssetIdentityVerificationResult> VerifyAsync(
        MediaAsset asset,
        Stream content,
        CancellationToken cancellationToken)
    {
        var expected = await works.FindAsync(asset.WorkId, cancellationToken);
        if (expected is null || string.IsNullOrWhiteSpace(expected.Title) ||
            string.IsNullOrWhiteSpace(expected.PrimaryAuthor))
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The catalog entry for this book has no title or author on file to compare against.");
        }

        try
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntryCount)
            {
                return AssetIdentityVerificationResult.Unmatched(
                    Id, "The EPUB contains an unusually large number of internal files, so it was not opened for comparison.");
            }

            var container = archive.GetEntry("META-INF/container.xml");
            if (container is null || container.Length > MaxMetadataEntryBytes)
            {
                return AssetIdentityVerificationResult.Unmatched(
                    Id, "The EPUB's container manifest (META-INF/container.xml) is missing or unusually large.");
            }

            var rootfilePath = await ReadRootfilePathAsync(container, cancellationToken);
            if (string.IsNullOrWhiteSpace(rootfilePath) || IsUnsafeArchivePath(rootfilePath))
            {
                return AssetIdentityVerificationResult.Unmatched(
                    Id, "The EPUB's container manifest did not point to a readable package file.");
            }

            var package = archive.GetEntry(rootfilePath);
            if (package is null || package.Length > MaxMetadataEntryBytes)
            {
                return AssetIdentityVerificationResult.Unmatched(
                    Id, "The EPUB's package file (content.opf) is missing or unusually large.");
            }

            var (titles, creators, languages) = await ReadPackageMetadataAsync(package, cancellationToken);
            var titleMatches = titles.Any(title => bookMatcher.TitleMatches(expected.Title, title));
            var authorMatches = creators.Any(creator => bookMatcher.AuthorMatches(expected.PrimaryAuthor, creator));
            if (!titleMatches || !authorMatches)
            {
                return AssetIdentityVerificationResult.Unmatched(Id, DescribeTitleAuthorMismatch(
                    expected.Title, expected.PrimaryAuthor, titles, creators, titleMatches, authorMatches));
            }

            // Consent is specific to the selected language, including English.
            // Missing metadata remains eligible; an explicit mismatch does not.
            var acceptedLanguage = await FindAcceptedLanguageAsync(asset.AssociatedRequestFormatId, cancellationToken);
            if (languages.Count > 0 &&
                !languages.Any(language => LanguageAcceptance.IsAcceptedOrUnspecified(language, acceptedLanguage)))
            {
                return AssetIdentityVerificationResult.Unmatched(
                    Id,
                    $"The file's embedded language ({string.Join(", ", languages)}) doesn't match the language " +
                    $"accepted for this request{(string.IsNullOrWhiteSpace(acceptedLanguage) ? string.Empty : $" ({acceptedLanguage})")}.");
            }

            return AssetIdentityVerificationResult.Match(Id);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or XmlException)
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The EPUB's metadata could not be read safely -- it may be malformed.");
        }
    }

    /// <summary>
    /// The one piece of information a librarian actually needs to decide
    /// whether this is the right book: the catalog's expectation next to
    /// what the file itself declares, for whichever of title/author didn't
    /// line up.
    /// </summary>
    private static string DescribeTitleAuthorMismatch(
        string expectedTitle,
        string expectedAuthor,
        IReadOnlyList<string> foundTitles,
        IReadOnlyList<string> foundCreators,
        bool titleMatches,
        bool authorMatches)
    {
        var parts = new List<string>();
        if (!titleMatches)
        {
            parts.Add(foundTitles.Count == 0
                ? $"the file has no embedded title (catalog expects \"{expectedTitle}\")"
                : $"the file's embedded title is \"{string.Join("\" / \"", foundTitles)}\" but the catalog expects \"{expectedTitle}\"");
        }

        if (!authorMatches)
        {
            parts.Add(foundCreators.Count == 0
                ? $"the file has no embedded author (catalog expects \"{expectedAuthor}\")"
                : $"the file's embedded author is \"{string.Join("\" / \"", foundCreators)}\" but the catalog expects \"{expectedAuthor}\"");
        }

        return $"Embedded metadata didn't match the catalog entry: {string.Join("; ", parts)}.";
    }

    private static async Task<string?> ReadRootfilePathAsync(
        ZipArchiveEntry container,
        CancellationToken cancellationToken)
    {
        var document = await ReadXmlAsync(container, cancellationToken);
        return document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")
            ?.Attribute("full-path")
            ?.Value
            .Trim();
    }

    private static async Task<(IReadOnlyList<string> Titles, IReadOnlyList<string> Creators, IReadOnlyList<string> Languages)>
        ReadPackageMetadataAsync(ZipArchiveEntry package, CancellationToken cancellationToken)
    {
        var document = await ReadXmlAsync(package, cancellationToken);
        return (
            document.Descendants()
                .Where(element => element.Name.LocalName == "title")
                .Select(element => element.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
            document.Descendants()
                .Where(element => element.Name.LocalName == "creator")
                .Select(element => element.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
            document.Descendants()
                .Where(element => element.Name.LocalName == "language")
                .Select(element => element.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray());
    }

    /// <summary>
    /// The language the requester already explicitly accepted for this
    /// specific format, if any.
    /// </summary>
    private async Task<string?> FindAcceptedLanguageAsync(Guid requestFormatId, CancellationToken cancellationToken)
    {
        var request = await requestFulfillment.FindByFormatIdAsync(requestFormatId, cancellationToken);
        return request?.Formats.SingleOrDefault(format => format.Id == requestFormatId)?.AcceptedLanguage;
    }

    private static async Task<XDocument> ReadXmlAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxMetadataEntryBytes
        });
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
    }

    private static bool IsUnsafeArchivePath(string path) =>
        Path.IsPathRooted(path) ||
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

}
