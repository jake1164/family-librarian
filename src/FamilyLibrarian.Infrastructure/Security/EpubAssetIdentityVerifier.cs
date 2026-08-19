using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Publishing;
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
public sealed class EpubAssetIdentityVerifier(IWorkLookup works) : IAssetIdentityVerifier
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
            return AssetIdentityVerificationResult.Unmatched(Id);
        }

        try
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntryCount)
            {
                return AssetIdentityVerificationResult.Unmatched(Id);
            }

            var container = archive.GetEntry("META-INF/container.xml");
            if (container is null || container.Length > MaxMetadataEntryBytes)
            {
                return AssetIdentityVerificationResult.Unmatched(Id);
            }

            var rootfilePath = await ReadRootfilePathAsync(container, cancellationToken);
            if (string.IsNullOrWhiteSpace(rootfilePath) || IsUnsafeArchivePath(rootfilePath))
            {
                return AssetIdentityVerificationResult.Unmatched(Id);
            }

            var package = archive.GetEntry(rootfilePath);
            if (package is null || package.Length > MaxMetadataEntryBytes)
            {
                return AssetIdentityVerificationResult.Unmatched(Id);
            }

            var (titles, creators) = await ReadPackageMetadataAsync(package, cancellationToken);
            var titleMatches = titles.Any(title => Matches(expected.Title, title));
            var authorMatches = creators.Any(creator => AuthorMatches(expected.PrimaryAuthor, creator));

            return titleMatches && authorMatches
                ? AssetIdentityVerificationResult.Match(Id)
                : AssetIdentityVerificationResult.Unmatched(Id);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or XmlException)
        {
            return AssetIdentityVerificationResult.Unmatched(Id);
        }
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

    private static async Task<(IReadOnlyList<string> Titles, IReadOnlyList<string> Creators)> ReadPackageMetadataAsync(
        ZipArchiveEntry package,
        CancellationToken cancellationToken)
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
                .ToArray());
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

    // Catalog titles and EPUB package metadata disagree on where a leading
    // article goes as often as Gutendex search results do (see
    // GutendexProvider.NormalizeTitleVariants) — e.g. a catalog title of
    // "Green Mummy" against an EPUB whose <dc:title> is "The Green Mummy".
    // Without stripping this, a genuinely correct download is held as a
    // false-negative "Unmatched" identity mismatch.
    private static readonly string[] LeadingArticles = ["The ", "A ", "An "];
    private static readonly string[] TrailingArticles = [", The", ", A", ", An"];

    // Same StartsWith comparison GutendexProvider.TitleMatches already
    // uses at discovery time, for the same reason: Gutenberg's older
    // catalog entries often carry a period-piece subtitle the family
    // catalog doesn't — e.g. an EPUB titled "Little Women; Or, Meg, Jo,
    // Beth, and Amy" against a catalog title of just "Little Women". A
    // download discovery already accepted as a legitimate prefix match must
    // not then be held as an identity mismatch by a stricter exact
    // comparison here.
    private static bool Matches(string expected, string actual)
    {
        var normalizedExpected = Normalize(NormalizeTitleVariants(expected));
        var normalizedActual = Normalize(NormalizeTitleVariants(actual));
        return !string.IsNullOrEmpty(normalizedExpected) &&
            normalizedActual.StartsWith(normalizedExpected, StringComparison.Ordinal);
    }

    private static string NormalizeTitleVariants(string value)
    {
        var trimmed = value.Replace("&", " and ", StringComparison.Ordinal).Trim();

        foreach (var article in TrailingArticles)
        {
            if (trimmed.EndsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^article.Length].TrimEnd();
                break;
            }
        }

        foreach (var article in LeadingArticles)
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[article.Length..];
            }
        }

        return trimmed;
    }

    // EPUB creators commonly use catalog order ("Mafi, Tahereh") while the
    // family catalog uses display order ("Tahereh Mafi"), and can carry a
    // fuller name than the catalog does (e.g. catalog "J. M. Barrie" vs an
    // EPUB crediting "Barrie, J. M. (James Matthew)") — see
    // GutendexProvider.AuthorMatches for the same reasoning. Requiring every
    // catalog token to appear in the EPUB's token set accepts that without
    // giving up the strict title comparison or accepting an unrelated name.
    private static bool AuthorMatches(string expected, string actual)
    {
        var expectedTokens = AuthorTokens(expected);
        var actualTokens = AuthorTokens(actual);
        return expectedTokens.Length > 0 && expectedTokens.All(actualTokens.Contains);
    }

    private static string[] AuthorTokens(string value)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();

        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        tokens.Sort(StringComparer.Ordinal);
        return tokens.ToArray();
    }

    private static string Normalize(string value) => new(value
        .Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
}
