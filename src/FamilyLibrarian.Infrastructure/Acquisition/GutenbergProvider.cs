using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Gutenberg;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>
/// Direct acquisition from the locally imported Project Gutenberg RDF catalogue.
/// Discovery does not call either Gutendex or the human-facing Gutenberg site.
/// </summary>
public sealed class GutenbergProvider(
    IGutenbergCatalog catalog,
    IProviderRegistry registry,
    IProviderSettingsStore settingsStore,
    IWorkLookup workLookup,
    IGutenbergFileResolver fileResolver,
    HttpClient httpClient,
    ManualImportPolicy importPolicy,
    IBookMatcher bookMatcher) : IAutomaticDirectAcquisitionProvider
{
    private const string AudioBundleFormat = "audio-bundle";

    public string Id => ProviderRegistry.GutenbergProviderId;

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        if (mediaType is not (RequestMediaType.Ebook or RequestMediaType.Audiobook))
        {
            return [];
        }

        var descriptor = registry.Find(Id);
        if (descriptor is null || !ProviderState.IsUsable(
                descriptor, await settingsStore.FindAsync(Id, cancellationToken)))
        {
            return [];
        }

        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null || string.IsNullOrWhiteSpace(work.Title))
        {
            return [];
        }

        var candidates = await catalog.SearchAsync(new GutenbergCatalogSearchQuery(
            work.Title,
            mediaType,
            RequireEpub: mediaType == RequestMediaType.Ebook,
            Take: 30), cancellationToken);

        foreach (var candidate in candidates)
        {
            if (!bookMatcher.TitleMatches(work.Title, candidate.Title) ||
                (!string.IsNullOrWhiteSpace(work.PrimaryAuthor) && !candidate.People
                    .Where(person => person.Role == GutenbergPersonRole.Author)
                    .Any(person => bookMatcher.AuthorMatches(work.PrimaryAuthor, person.Name))))
            {
                continue;
            }

            var option = mediaType == RequestMediaType.Ebook
                ? BuildEbookOption(candidate, workId)
                : BuildAudiobookOption(candidate, workId);
            if (option is not null)
            {
                return [option];
            }
        }

        return [];
    }

    public async Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
        FulfillmentOption fulfillmentOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fulfillmentOption);
        var reference = JsonSerializer.Deserialize<GutenbergDownloadReference>(fulfillmentOption.ProviderData ?? string.Empty)
            ?? throw new InvalidOperationException("This Gutenberg option has no local-catalog download reference.");
        if (reference.SourcePaths.Length == 0)
        {
            throw new InvalidOperationException("This Gutenberg option has no downloadable formats.");
        }

        if (fulfillmentOption.Format != AudioBundleFormat)
        {
            var stream = await OpenFromMirrorsAsync(reference.SourcePaths[0], reference.FormatKind, cancellationToken);
            return [new DirectAcquisitionFile(stream, $"gutenberg-{fulfillmentOption.ProviderResultId}.epub")];
        }

        if (reference.SourcePaths.Length > importPolicy.MaxAudiobookBundleTracks)
        {
            throw new InvalidOperationException("The Gutenberg audiobook exceeds the configured track limit.");
        }

        IReadOnlyList<DirectAcquisitionFile> files = reference.SourcePaths.Select((path, index) => new DirectAcquisitionFile(
            new LazyMirrorStream(token => OpenFromMirrorsAsync(path, reference.FormatKind, token)),
            $"gutenberg-{fulfillmentOption.ProviderResultId}-{index + 1:00}.mp3")).ToArray();
        return files;
    }

    private FulfillmentOption? BuildEbookOption(GutenbergCatalogBook book, Guid workId)
    {
        var format = book.Formats.OrderBy(format => format.Kind switch
            {
                GutenbergFormatKind.Epub3Images => 0,
                GutenbergFormatKind.EpubImages => 1,
                GutenbergFormatKind.EpubNoImages => 2,
                _ => 3
            })
            .FirstOrDefault(format => format.Kind is GutenbergFormatKind.Epub3Images or
                GutenbergFormatKind.EpubImages or GutenbergFormatKind.EpubNoImages);
        return format is null ? null : CreateOption(book, workId, RequestMediaType.Ebook, "epub", [format.SourcePath], format.Kind);
    }

    private FulfillmentOption? BuildAudiobookOption(GutenbergCatalogBook book, Guid workId)
    {
        var tracks = book.Formats.Where(format => format.Kind == GutenbergFormatKind.AudioMp3)
            .OrderBy(format => format.SourcePath, StringComparer.Ordinal).ToArray();
        return tracks.Length == 0 ? null : CreateOption(
            book, workId, RequestMediaType.Audiobook, AudioBundleFormat,
            tracks.Select(track => track.SourcePath).ToArray(), GutenbergFormatKind.AudioMp3);
    }

    private FulfillmentOption CreateOption(
        GutenbergCatalogBook book,
        Guid workId,
        RequestMediaType mediaType,
        string format,
        string[] sourcePaths,
        GutenbergFormatKind formatKind) => new(
        Id,
        book.GutenbergId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        workId,
        EditionId: null,
        mediaType,
        OptionKind.DirectAcquisition,
        AcquisitionMethod.DirectDownload,
        format,
        book.Languages.Count == 0 ? null : book.Languages[0],
        Quality: null,
        Availability: null,
        Cost: 0m,
        Currency: null,
        LicenseOrUsageStatus: "Public domain",
        DrmStatus: null,
        ExternalActionUri: null,
        JsonSerializer.Serialize(new GutenbergDownloadReference(formatKind, sourcePaths)));

    private async Task<Stream> OpenFromMirrorsAsync(
        string sourcePath,
        GutenbergFormatKind formatKind,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        foreach (var uri in fileResolver.Resolve(sourcePath, formatKind))
        {
            try
            {
                var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    continue;
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return new ResponseStream(stream, response);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                failure = exception;
            }
        }

        throw new HttpRequestException("No configured Project Gutenberg mirror could provide the requested file.", failure);
    }

    private sealed record GutenbergDownloadReference(GutenbergFormatKind FormatKind, string[] SourcePaths);

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }

    private sealed class LazyMirrorStream(Func<CancellationToken, Task<Stream>> open) : Stream
    {
        private Stream? inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            inner ??= await open(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner?.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { if (inner is not null) await inner.DisposeAsync(); await base.DisposeAsync(); }
    }

}
