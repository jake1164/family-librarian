using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>One MP3 chapter file for a Gutenberg "Sound" (human-read audiobook) record.</summary>
public sealed record GutenbergAudioTrack(Uri Url, string Extension);

/// <summary>
/// Resolves the ordered list of chapter audio files for a Gutenberg
/// audiobook, straight from Gutenberg's own per-book RDF catalog record.
/// </summary>
/// <remarks>
/// Gutendex's JSON <c>formats</c> dictionary is keyed by MIME type, so a
/// multi-chapter audiobook's several <c>audio/mpeg</c> files collapse to a
/// single URL there — it can prove a Sound record exists, but not enumerate
/// its tracks. Gutenberg's RDF (<c>cache/epub/{id}/pg{id}.rdf</c>) lists
/// every <c>pgterms:file</c> individually, so this reads that instead.
/// </remarks>
public interface IGutenbergAudiobookCatalog
{
    Task<IReadOnlyList<GutenbergAudioTrack>> FindTracksAsync(int gutenbergId, CancellationToken cancellationToken);
}

public sealed class GutenbergAudiobookRdfClient(HttpClient httpClient) : IGutenbergAudiobookCatalog
{
    private const string Mp3MimeType = "audio/mpeg";

    // An RDF record's size is dominated by its subject/bookshelf metadata,
    // not its file list; this comfortably covers even a heavily-tagged
    // multi-format audiobook while still bounding untrusted remote XML.
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly XNamespace RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace PgTermsNamespace = "http://www.gutenberg.org/2009/pgterms/";
    private static readonly XNamespace DcTermsNamespace = "http://purl.org/dc/terms/";

    public async Task<IReadOnlyList<GutenbergAudioTrack>> FindTracksAsync(
        int gutenbergId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"cache/epub/{gutenbergId}/pg{gutenbergId}.rdf",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        XDocument document;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var bounded = new BoundedReadStream(body, MaxResponseBytes);
            using var reader = XmlReader.Create(bounded, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException)
        {
            // A malformed catalog record is a provider-health concern, not a
            // genuine "no tracks" answer, but this is a best-effort discovery
            // aid: degrade to no tracks and let the caller treat it as no
            // automatic candidate rather than failing the whole request.
            return [];
        }
        catch (InvalidOperationException)
        {
            // Raised by BoundedReadStream when the response exceeds the cap.
            return [];
        }

        return document
            .Descendants(PgTermsNamespace + "file")
            .Where(file => file
                .Descendants(DcTermsNamespace + "format")
                .Descendants(RdfNamespace + "value")
                .Any(value => string.Equals(value.Value.Trim(), Mp3MimeType, StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.Attribute(RdfNamespace + "about")?.Value)
            .Where(url => !string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
            .Select(url => new Uri(url!, UriKind.Absolute))
            .OrderBy(url => url.ToString(), StringComparer.Ordinal)
            .Select(url => new GutenbergAudioTrack(url, Path.GetExtension(url.LocalPath)))
            .ToArray();
    }

    /// <summary>Aborts the read once more than <paramref name="maxBytes"/> have been read.</summary>
    private sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _totalRead += read;
            if (_totalRead > maxBytes)
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Response exceeded {maxBytes} bytes."));
            }

            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
