using System.Globalization;
using System.Text;
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
    // Real, deterministically-ranked audiobook formats Project Gutenberg's
    // audio mirrors actually serve (see BuildAudiobookOptions). Anything else
    // reported by the RDF catalogue (legacy Speex, WAV, ...) is classified as
    // GutenbergFormatKind.Other by the synchronizer and never reaches here.
    private static readonly Dictionary<GutenbergFormatKind, string> AudiobookFormatLabels =
        new()
        {
            [GutenbergFormatKind.AudioM4b] = "m4b",
            [GutenbergFormatKind.AudioMp3] = "mp3",
            [GutenbergFormatKind.AudioOgg] = "ogg"
        };

    // See PickDominantByPopularity's remarks for why these exist and what
    // they deliberately do not do.
    internal const int MinimumDominantDownloadCount = 1_000;
    internal const double DominantDownloadRatio = 3.0;

    public string Id => ProviderRegistry.GutenbergProviderId;

    /// <summary>Not ready while the local RDF catalogue is still (re)importing — see <see cref="IDirectAcquisitionProvider.IsReadyAsync"/>.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
        (await catalog.GetStatusAsync(cancellationToken)).IsReady;

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null || string.IsNullOrWhiteSpace(work.Title))
        {
            return [];
        }

        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s);
        var options = await FindDirectAcquisitionsCoreAsync(identity, mediaType, cancellationToken);
        return options.Select(option => option with { WorkId = workId }).ToArray();
    }

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        BookIdentity identity,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.Title))
        {
            return [];
        }

        return await FindDirectAcquisitionsCoreAsync(identity, mediaType, cancellationToken);
    }

    private async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsCoreAsync(
        BookIdentity identity,
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

        var candidates = await catalog.SearchAsync(new GutenbergCatalogSearchQuery(
            identity.Title,
            mediaType,
            RequireEpub: mediaType == RequestMediaType.Ebook,
            Take: 30), cancellationToken);

        // Scan every title/author match rather than stopping at the first one:
        // an early return here (the old behavior) could return a foreign
        // translation while a later candidate is the requested English
        // edition, and even among English/unspecified candidates there can be
        // more than one plausible edition -- both English-eligible and
        // non-English matches are collected and left for the caller
        // (AutomaticRequestFulfillmentService) to decide between: a single
        // eligible match auto-acquires, more than one becomes a SELFSERV-1
        // preference choice, and a non-English match is kept, marked
        // RequiresLanguageConfirmation, for the same preference-choice
        // treatment instead of silently auto-acquiring (or discarding) it.
        var autoEligible = new List<(FulfillmentOption Option, int? DownloadCount)>();
        var languageExcluded = new List<FulfillmentOption>();
        foreach (var candidate in candidates)
        {
            var matchedAuthor = candidate.People
                .Where(person => person.Role == GutenbergPersonRole.Author)
                .FirstOrDefault(person => !string.IsNullOrWhiteSpace(identity.Author) &&
                    bookMatcher.AuthorMatches(identity.Author, person.Name));
            if (!bookMatcher.TitleMatches(identity.Title, candidate.Title) ||
                (!string.IsNullOrWhiteSpace(identity.Author) && matchedAuthor is null))
            {
                continue;
            }

            var option = mediaType == RequestMediaType.Ebook
                ? BuildEbookOption(candidate)
                : await BuildBestAudiobookOptionAsync(candidate, cancellationToken);
            if (option is null)
            {
                continue;
            }

            option = option with
            {
                Title = candidate.Title,
                Author = matchedAuthor?.Name ?? candidate.People
                    .FirstOrDefault(person => person.Role == GutenbergPersonRole.Author)?.Name
            };

            if (LanguageAcceptance.IsEnglishOrUnspecified(option.Language))
            {
                autoEligible.Add((option, candidate.DownloadCount));
            }
            else
            {
                languageExcluded.Add(option with { RequiresLanguageConfirmation = true });
            }
        }

        // Download-count dominance decides only between ambiguous EBOOK
        // editions of the same text (see PickDominantByPopularity's remarks).
        // An audiobook's "which record" ambiguity is a narration/completeness
        // decision now, made by AudiobookCandidateSelector at the caller --
        // popularity must not gate whether an audiobook can auto-acquire at
        // all (a 17-download recording can still be the one clearly correct
        // copy), only ever break a genuine tie there, very late.
        if (mediaType == RequestMediaType.Ebook &&
            autoEligible.Count > 1 && PickDominantByPopularity(autoEligible) is { } dominant)
        {
            return [dominant];
        }

        return autoEligible.Count > 0
            ? autoEligible.Select(entry => entry.Option).ToArray()
            : languageExcluded;
    }

    // Multiple same-language editions of a public-domain title are common on
    // Gutenberg (independent transcriptions/reprints over the decades) and
    // are not all equally "the" edition most people mean -- e.g. Moby-Dick
    // has separate entries at #15 (1991, ~4k downloads), #2489 (2001, ~25k),
    // and #2701 (2001, ~164k). Rather than always asking a human to choose
    // between editions that are, in practice, interchangeable copies of the
    // same text, auto-resolve to the one with a clearly dominant download
    // count -- Project Gutenberg's own usage signal for which entry the
    // community treats as the standard one -- and keep asking only when no
    // candidate dominates by this margin. This is a deliberate, narrow
    // carve-out from SELFSERV-1's "always ask on ambiguity" default, not a
    // reversal of it: it never crosses a language boundary (only ever
    // compares within the already-English-filtered autoEligible set), and a
    // missing or unimpressive download count just falls through to asking,
    // same as before.
    private static FulfillmentOption? PickDominantByPopularity(
        IReadOnlyList<(FulfillmentOption Option, int? DownloadCount)> candidates)
    {
        var ranked = candidates.OrderByDescending(entry => entry.DownloadCount ?? 0).ToArray();
        var top = ranked[0];
        if (top.DownloadCount is not { } topCount || topCount < MinimumDominantDownloadCount)
        {
            return null;
        }

        var runnerUpCount = ranked[1].DownloadCount ?? 0;
        if (topCount < runnerUpCount * DominantDownloadRatio)
        {
            return null;
        }

        var dominance = runnerUpCount == 0
            ? "the runner-up has no reported downloads"
            : $"{topCount / (double)runnerUpCount:0.#}× the runner-up record " +
              $"#{ranked[1].Option.ProviderResultId} " +
              $"({runnerUpCount.ToString("N0", CultureInfo.InvariantCulture)} downloads)";
        return top.Option with
        {
            AutomaticSelectionReason =
                $"Selected automatically: Project Gutenberg record #{top.Option.ProviderResultId} has " +
                $"{topCount.ToString("N0", CultureInfo.InvariantCulture)} downloads, and {dominance}. " +
                $"The rule requires at least {MinimumDominantDownloadCount.ToString("N0", CultureInfo.InvariantCulture)} downloads " +
                $"and a {DominantDownloadRatio:0.#}× lead."
        };
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

        if (fulfillmentOption.MediaType != RequestMediaType.Audiobook)
        {
            var stream = await OpenFromMirrorsAsync(reference.SourcePaths[0], reference.FormatKind, cancellationToken);
            return [new DirectAcquisitionFile(stream, $"gutenberg-{fulfillmentOption.ProviderResultId}.epub")];
        }

        if (reference.SourcePaths.Length > importPolicy.MaxAudiobookBundleTracks)
        {
            throw new InvalidOperationException(
                $"This Gutenberg audiobook has {reference.SourcePaths.Length} tracks, over the configured " +
                $"track limit of {importPolicy.MaxAudiobookBundleTracks}. Raise " +
                $"{ManualImportPolicy.SectionName}:{nameof(ManualImportPolicy.MaxAudiobookBundleTracks)} " +
                "in configuration and restart the app to allow automatic acquisition, or upload the audiobook " +
                "file manually from this request's Files section.");
        }

        var extension = AudiobookFormatLabels.GetValueOrDefault(reference.FormatKind, "mp3");
        IReadOnlyList<DirectAcquisitionFile> files = reference.SourcePaths.Select((path, index) => new DirectAcquisitionFile(
            new LazyMirrorStream(token => OpenFromMirrorsAsync(path, reference.FormatKind, token)),
            $"gutenberg-{fulfillmentOption.ProviderResultId}-{index + 1:00}.{extension}")).ToArray();
        return files;
    }

    private FulfillmentOption? BuildEbookOption(GutenbergCatalogBook book)
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
        return format is null ? null : CreateOption(
            book, RequestMediaType.Ebook, "epub", [format.SourcePath], format.Kind,
            sizeBytes: format.FileSizeBytes);
    }

    /// <summary>
    /// Builds one bundle option per real codec this record actually publishes
    /// (Project Gutenberg audio editions commonly ship MP3, M4B, and Ogg
    /// Vorbis side by side), then keeps only the one <see cref="AudiobookFormatPolicy"/>
    /// ranks highest -- e.g. M4B's much smaller, chaptered files over a
    /// needlessly large MP3 bundle -- so the rest of this method still sees
    /// exactly one option per Gutenberg record, same as every other kind.
    /// </summary>
    private async Task<FulfillmentOption?> BuildBestAudiobookOptionAsync(
        GutenbergCatalogBook book, CancellationToken cancellationToken)
    {
        var options = await BuildAudiobookOptionsAsync(book, cancellationToken);
        var best = AudiobookFormatPolicy.KeepHighestUsable(options);
        return best.Count == 0 ? null : best[0];
    }

    private async Task<FulfillmentOption[]> BuildAudiobookOptionsAsync(
        GutenbergCatalogBook book, CancellationToken cancellationToken)
    {
        var groups = book.Formats
            .Where(format => AudiobookFormatLabels.ContainsKey(format.Kind))
            .GroupBy(format => format.Kind)
            .ToArray();
        if (groups.Length == 0)
        {
            return [];
        }

        // One recording can be bundled as several codecs, but they are all
        // the same reading -- fetch the narration credit once per book, not
        // once per codec bundle.
        var narration = await TryFetchNarrationEvidenceAsync(book, cancellationToken);

        return groups.Select(group =>
        {
            var tracks = group.OrderBy(format => format.SourcePath, StringComparer.Ordinal).ToArray();
            return CreateOption(
                book, RequestMediaType.Audiobook, AudiobookFormatLabels[group.Key],
                tracks.Select(track => track.SourcePath).ToArray(), group.Key,
                sizeBytes: SumKnownSizes(tracks), partCount: tracks.Length) with
            {
                NarrationKind = narration.Kind,
                Narrator = narration.Narrator,
                NarrationEvidence = narration.Evidence
            };
        }).ToArray();
    }

    /// <summary>
    /// Project Gutenberg's RDF/bibliographic metadata does not distinguish
    /// human from computer-generated narration (checked against real records
    /// before writing this -- see <see cref="GutenbergNarrationParser"/>'s
    /// remarks), but every audio record's own <c>*readme.txt</c> commonly
    /// states it in prose. A fetch failure here degrades to
    /// <see cref="NarrationKind.Unknown"/> rather than failing the whole
    /// candidate -- narration is enrichment, not a required field.
    /// </summary>
    private async Task<GutenbergNarrationEvidence> TryFetchNarrationEvidenceAsync(
        GutenbergCatalogBook book, CancellationToken cancellationToken)
    {
        var readme = book.Formats.FirstOrDefault(format =>
            format.SourcePath.EndsWith("readme.txt", StringComparison.OrdinalIgnoreCase));
        if (readme is null)
        {
            return GutenbergNarrationEvidence.Unknown;
        }

        try
        {
            await using var stream = await OpenFromMirrorsAsync(readme.SourcePath, GutenbergFormatKind.Other, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // The narration statement always appears near the top -- reading
            // a capped prefix avoids holding a whole file in memory for a
            // value only ever found in its first few kilobytes.
            var buffer = new char[16 * 1024];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            return GutenbergNarrationParser.Parse(new string(buffer, 0, read));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return GutenbergNarrationEvidence.Unknown;
        }
    }

    private FulfillmentOption CreateOption(
        GutenbergCatalogBook book,
        RequestMediaType mediaType,
        string format,
        string[] sourcePaths,
        GutenbergFormatKind formatKind,
        long? sizeBytes = null,
        int? partCount = null) => new(
        Id,
        book.GutenbergId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        WorkId: Guid.Empty,
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
        JsonSerializer.Serialize(new GutenbergDownloadReference(formatKind, sourcePaths)),
        SizeBytes: sizeBytes,
        PartCount: partCount,
        ProviderPopularity: book.DownloadCount,
        AdminInspectionUri: new Uri($"https://www.gutenberg.org/ebooks/{book.GutenbergId}"));

    private static long? SumKnownSizes(IReadOnlyList<GutenbergCatalogFormat> formats) =>
        formats.Any(format => format.FileSizeBytes is null)
            ? null
            : formats.Sum(format => format.FileSizeBytes!.Value);

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
