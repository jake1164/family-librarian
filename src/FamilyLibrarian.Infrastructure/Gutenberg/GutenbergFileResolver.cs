using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Gutenberg;

public interface IGutenbergFileResolver
{
    IReadOnlyList<Uri> Resolve(string sourcePath, GutenbergFormatKind formatKind);
}

public sealed class GutenbergMirrorOptions
{
    public const string SectionName = "GutenbergMirrors";

    public List<string> BaseUris { get; set; } =
    [
        "https://gutenberg.pglaf.org/",
        "https://mirror.cs.odu.edu/gutenberg/",
        // Some Project Gutenberg audio files are intentionally served only
        // through the public web endpoint, not copied to the file mirrors.
        "https://www.gutenberg.org/"
    ];
}

public sealed class GutenbergFileResolver(GutenbergMirrorOptions options) : IGutenbergFileResolver
{
    public IReadOnlyList<Uri> Resolve(string sourcePath, GutenbergFormatKind formatKind)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !sourcePath.StartsWith('/'))
        {
            return [];
        }

        return options.BaseUris
            .Select(value => ResolveUri(value, sourcePath, formatKind))
            .OfType<Uri>()
            .Distinct()
            .ToArray();
    }

    private static Uri? ResolveUri(string value, string sourcePath, GutenbergFormatKind formatKind)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // The public website retains the RDF catalogue's /files layout. The
        // rsync-compatible mirrors use their split-digit directory layout.
        var relativePath = string.Equals(baseUri.Host, "www.gutenberg.org", StringComparison.OrdinalIgnoreCase)
            ? sourcePath
            : NormalizePath(sourcePath, formatKind);
        return Uri.TryCreate(baseUri, relativePath.TrimStart('/'), out var resolved) ? resolved : null;
    }

    private static string NormalizePath(string sourcePath, GutenbergFormatKind formatKind)
    {
        if (sourcePath.StartsWith("/cache/generated/", StringComparison.OrdinalIgnoreCase))
        {
            return "/cache/epub/" + sourcePath[17..];
        }

        var segments = sourcePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 &&
            string.Equals(segments[0], "files", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[1], out var filesBookId) &&
            filesBookId > 0)
        {
            // The RDF catalogue uses Gutenberg's public web path
            // (/files/{id}/...), while its rsync-compatible mirrors store the
            // main collection under one directory per digit (/1/2/3/{id}/...).
            // Generated formats remain under /cache/epub and are handled below.
            var splitDirectory = string.Join('/', segments[1].Select(digit => digit.ToString()));
            return $"/{splitDirectory}/{filesBookId}/{string.Join('/', segments[2..])}";
        }

        if (segments.Length == 2 && string.Equals(segments[0], "ebooks", StringComparison.OrdinalIgnoreCase))
        {
            var token = segments[1];
            var id = token.Split('.', 2)[0];
            if (int.TryParse(id, out _))
            {
                var filename = formatKind switch
                {
                    GutenbergFormatKind.Epub3Images => $"pg{id}-images-3.epub",
                    GutenbergFormatKind.EpubImages => $"pg{id}-images.epub",
                    GutenbergFormatKind.EpubNoImages => $"pg{id}.epub",
                    _ => token
                };
                return $"/cache/epub/{id}/{filename}";
            }
        }

        return sourcePath;
    }
}
