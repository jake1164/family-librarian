using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.LibriVox;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Infrastructure.Acquisition;

/// <summary>Built-in public-domain audiobook search and acquisition from LibriVox.</summary>
public sealed class LibriVoxProvider(
    LibriVoxApiClient api,
    HttpClient downloadClient,
    IProviderRegistry registry,
    IProviderSettingsStore settingsStore,
    IWorkLookup workLookup,
    IBookMatcher matcher,
    ManualImportPolicy importPolicy,
    LibriVoxDownloadWorkspace downloadWorkspace) : IAutomaticDirectAcquisitionProvider, IProgressReportingDirectAcquisitionProvider, IDisposable
{
    private const long MaximumZipBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> HarmlessMetadataExtensions = [".txt", ".jpg", ".jpeg", ".pdf"];
    private readonly ConcurrentDictionary<Guid, FileStream> acquisitionLocks = new();
    public string Id => ProviderRegistry.LibriVoxProviderId;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null || string.IsNullOrWhiteSpace(work.Title)) return [];
        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s);
        var options = await FindDirectAcquisitionsAsync(identity, mediaType, cancellationToken);
        return options.Select(option => option with { WorkId = workId }).ToArray();
    }

    public async Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Audiobook || string.IsNullOrWhiteSpace(identity.Title)) return [];
        var descriptor = registry.Find(Id);
        if (descriptor is null || !ProviderState.IsUsable(descriptor, await settingsStore.FindAsync(Id, cancellationToken))) return [];

        // Deliberately title-only: some combined title+author queries return HTTP 500.
        var recordings = await api.SearchByTitleAsync(identity.Title, cancellationToken);
        var options = new List<FulfillmentOption>();
        foreach (var recording in recordings)
        {
            if (!matcher.TitleMatches(identity.Title, recording.Title)) continue;
            var matchedAuthor = recording.Authors.FirstOrDefault(author =>
                string.IsNullOrWhiteSpace(identity.Author) || matcher.AuthorMatches(identity.Author, author));
            if (!string.IsNullOrWhiteSpace(identity.Author) && matchedAuthor is null) continue;

            var language = NormalizeLanguage(recording.Language);
            var readers = recording.Readers;
            var readerDisplay = readers.Count == 0 ? null : string.Join(", ", readers);
            options.Add(new FulfillmentOption(
                Id, recording.Id, Guid.Empty, null, RequestMediaType.Audiobook,
                OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload, "mp3", language,
                Quality: null, Availability: "Available", Cost: 0m, Currency: null,
                LicenseOrUsageStatus: "Public domain", DrmStatus: "none", ExternalActionUri: null,
                ProviderData: JsonSerializer.Serialize(new DownloadReference(recording.ZipUri.AbsoluteUri)),
                Title: recording.Title, Author: matchedAuthor ?? (recording.Authors.Count > 0 ? recording.Authors[0] : null),
                PublicationYear: recording.PublicationYear,
                PartCount: recording.SectionCount,
                IsUnabridged: null,
                AdminInspectionUri: SafeProjectUri(recording.ProjectUrl),
                NarrationKind: readers.Count > 0 ? NarrationKind.Human : NarrationKind.Unknown,
                Narrator: readerDisplay,
                NarrationEvidence: readers.Count > 0
                    ? $"LibriVox section reader credits: {readerDisplay}"
                    : null,
                RuntimeSeconds: recording.DurationSeconds,
                SectionTitles: recording.SectionTitles,
                NarratorNames: recording.Readers,
                SourceGenres: recording.Genres,
                CoverArtUri: recording.CoverUri));
        }

        var eligible = options.Where(option => LanguageAcceptance.IsEnglishOrUnspecified(option.Language)).ToArray();
        return eligible.Length > 0
            ? eligible
            : options.Select(option => option with { RequiresLanguageConfirmation = true }).ToArray();
    }

    public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
        FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
        FetchCoreAsync(fulfillmentOption, null, null, cancellationToken);

    public async Task<IReadOnlyList<DirectAcquisitionFile>> FetchWithProgressAsync(
        FulfillmentOption fulfillmentOption,
        DirectAcquisitionRequestContext requestContext,
        Action<DirectAcquisitionTransferProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        FileStream workspaceLock;
        try
        {
            workspaceLock = downloadWorkspace.AcquireLock(requestContext.RequestFormatId);
        }
        catch (IOException exception)
        {
            throw new ResumableDownloadInterruptedException(
                "Another acquisition currently owns this audiobook download workspace.", exception);
        }

        if (!acquisitionLocks.TryAdd(requestContext.RequestFormatId, workspaceLock))
        {
            await workspaceLock.DisposeAsync();
            throw new ResumableDownloadInterruptedException(
                "Another acquisition currently owns this audiobook download workspace.");
        }

        try
        {
            return await FetchCoreAsync(fulfillmentOption, requestContext, reportProgress, cancellationToken);
        }
        catch (ResumableDownloadInterruptedException)
        {
            ReleaseLock(requestContext.RequestFormatId);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseLock(requestContext.RequestFormatId);
            throw;
        }
        catch
        {
            ReleaseLock(requestContext.RequestFormatId);
            downloadWorkspace.Delete(requestContext.RequestFormatId);
            throw;
        }
    }

    public Task FinishAcquisitionAsync(
        DirectAcquisitionRequestContext requestContext)
    {
        ReleaseLock(requestContext.RequestFormatId);
        downloadWorkspace.Delete(requestContext.RequestFormatId);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var requestFormatId in acquisitionLocks.Keys)
            ReleaseLock(requestFormatId);
    }

    private void ReleaseLock(Guid requestFormatId)
    {
        if (acquisitionLocks.TryRemove(requestFormatId, out var workspaceLock))
            workspaceLock.Dispose();
    }

    private async Task<IReadOnlyList<DirectAcquisitionFile>> FetchCoreAsync(
        FulfillmentOption fulfillmentOption,
        DirectAcquisitionRequestContext? requestContext,
        Action<DirectAcquisitionTransferProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (fulfillmentOption.MediaType != RequestMediaType.Audiobook)
            throw new InvalidOperationException("LibriVox provides audiobook files only.");
        var reference = JsonSerializer.Deserialize<DownloadReference>(fulfillmentOption.ProviderData ?? string.Empty)
            ?? throw new InvalidOperationException("This LibriVox option has no download reference.");
        if (!Uri.TryCreate(reference.ZipUrl, UriKind.Absolute, out var uri) || !IsAllowedRemoteUri(uri))
            throw new InvalidOperationException("The LibriVox download address is invalid.");

        var isResumable = requestContext is not null;
        var directory = requestContext is { } context
            ? downloadWorkspace.DirectoryFor(context.RequestFormatId)
            : Path.Combine(Path.GetTempPath(), $"family-librarian-librivox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, "recording.zip");
        var extractionDirectory = Path.Combine(directory, "extracted");
        LibriVoxDownloadWorkspace.DeleteDirectory(extractionDirectory);
        Directory.CreateDirectory(extractionDirectory);
        var extracted = new List<string>();
        long archiveBytes;
        try
        {
            archiveBytes = await DownloadArchiveAsync(
                uri, archivePath, requestContext, fulfillmentOption.ProviderResultId, reportProgress, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            LibriVoxDownloadWorkspace.DeleteDirectory(extractionDirectory);
            if (requestContext is { } invalidContext) downloadWorkspace.Delete(invalidContext.RequestFormatId);
            else TryDeleteDirectory(directory);
            throw new InvalidOperationException("The audiobook archive failed safety checks or could not be processed.", exception);
        }
        reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
            archiveBytes, archiveBytes, "Preparing audiobook", IsTransferBaseline: true));
        try
        {
            using var archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > importPolicy.MaxAudiobookBundleTracks + 100)
                throw new InvalidOperationException("The LibriVox archive contains too many entries.");

            long totalExpanded = 0;
            var audioEntries = new List<ZipArchiveEntry>();
            foreach (var entry in archive.Entries)
            {
                ValidateEntryPath(entry.FullName);
                if (IsSymbolicLink(entry)) throw new InvalidDataException("The LibriVox archive contains a symbolic link.");
                if (entry.FullName.EndsWith('/')) continue;
                var extension = Path.GetExtension(entry.Name);
                if (string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length <= 0 || entry.Length > importPolicy.MaxUploadSizeBytes)
                        throw new InvalidDataException("A LibriVox MP3 track is empty or exceeds the configured per-file limit.");
                    totalExpanded = checked(totalExpanded + entry.Length);
                    if (totalExpanded > MaximumExpandedBytes) throw new InvalidDataException("The LibriVox archive exceeds the expanded-size limit.");
                    audioEntries.Add(entry);
                }
                else if (!HarmlessMetadataExtensions.Contains(extension))
                {
                    throw new InvalidDataException("The LibriVox archive contains an unsupported file type.");
                }
            }

            if (audioEntries.Count is 0 || audioEntries.Count > importPolicy.MaxAudiobookBundleTracks)
                throw new InvalidDataException("The LibriVox archive has no MP3 tracks or exceeds the configured audiobook track limit.");

            for (var index = 0; index < audioEntries.Count; index++)
            {
                var outputPath = Path.Combine(extractionDirectory, $"track-{index + 1:000}.mp3");
                await using var source = audioEntries[index].Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyBoundedAsync(source, output, audioEntries[index].Length, cancellationToken);
                extracted.Add(outputPath);
            }
        }
        catch (Exception exception)
        {
            LibriVoxDownloadWorkspace.DeleteDirectory(extractionDirectory);
            if (!isResumable) TryDeleteDirectory(directory);
            if (exception is InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                if (requestContext is { } invalidContext) downloadWorkspace.Delete(invalidContext.RequestFormatId);
                throw new InvalidOperationException("The LibriVox archive failed safety checks or could not be processed.", exception);
            }
            if (exception is InvalidOperationException && requestContext is { } invalidOperationContext)
                downloadWorkspace.Delete(invalidOperationContext.RequestFormatId);
            throw;
        }

        if (!isResumable) File.Delete(archivePath);
        return extracted.Select((path, index) => new DirectAcquisitionFile(
            new DeleteOnDisposeFileStream(path, extracted.Count == index + 1
                ? isResumable ? extractionDirectory : directory
                : null),
            $"librivox-{SanitizeId(fulfillmentOption.ProviderResultId)}-{index + 1:000}.mp3")).ToArray();
    }

    private async Task<long> DownloadArchiveAsync(
        Uri uri,
        string destination,
        DirectAcquisitionRequestContext? requestContext,
        string providerResultId,
        Action<DirectAcquisitionTransferProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        var partialPath = destination + ".part";
        var manifestPath = destination + ".resume.json";
        var manifest = TryReadManifest(manifestPath);
        var resumable = requestContext is { } context && manifest is not null &&
            manifest.RequestId == context.RequestId && manifest.RequestFormatId == context.RequestFormatId &&
            string.Equals(manifest.ProviderResultId, providerResultId, StringComparison.Ordinal) &&
            string.Equals(manifest.OriginalUri, uri.AbsoluteUri, StringComparison.Ordinal) &&
            Uri.TryCreate(manifest.DownloadUri, UriKind.Absolute, out var manifestUri) && IsAllowedRemoteUri(manifestUri);

        if (File.Exists(destination))
        {
            var length = new FileInfo(destination).Length;
            if (resumable && length is > 0 and <= MaximumZipBytes &&
                (manifest!.TotalBytes is null || manifest.TotalBytes == length))
            {
                reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
                    length, manifest.TotalBytes ?? length, "Preparing audiobook", IsTransferBaseline: true));
                return length;
            }
            TryDeleteFile(destination);
        }

        long offset = 0;
        if (resumable && File.Exists(partialPath))
        {
            offset = new FileInfo(partialPath).Length;
            resumable = offset > 0 && offset <= MaximumZipBytes &&
                (manifest!.ETag is not null || manifest.LastModifiedUtc is not null);
            if (resumable && manifest!.TotalBytes is { } total && offset > total) resumable = false;
            if (resumable && manifest!.TotalBytes == offset)
            {
                File.Move(partialPath, destination, overwrite: true);
                reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
                    offset, offset, "Preparing audiobook", IsTransferBaseline: true));
                return offset;
            }
        }

        if (!resumable)
        {
            offset = 0;
            TryDeleteFile(partialPath);
            TryDeleteFile(manifestPath);
            manifest = null;
        }

        for (var freshRetry = 0; freshRetry < 2; freshRetry++)
        {
            var isResumeRequest = resumable && manifest is not null;
            var requestUri = isResumeRequest ? new Uri(manifest!.DownloadUri) : uri;
            var restartFromBeginning = false;
            if (isResumeRequest)
            {
                reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
                    offset, manifest!.TotalBytes, "Downloading", IsTransferBaseline: true));
            }
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (isResumeRequest)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = manifest!.ETag is { } etag
                        ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag))
                        : new RangeConditionHeaderValue(manifest!.LastModifiedUtc!.Value);
                }

                HttpResponseMessage response;
                try
                {
                    response = await downloadClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (Exception exception) when (requestContext is not null && !cancellationToken.IsCancellationRequested &&
                    exception is HttpRequestException or TaskCanceledException)
                {
                    throw new ResumableDownloadInterruptedException(
                        "The audiobook archive connection was interrupted; the partial file was retained.", exception);
                }
                using (response)
                {
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location ?? throw new HttpRequestException("The audiobook archive redirect had no destination.");
                    requestUri = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
                    if (!IsAllowedRemoteUri(requestUri))
                        throw new HttpRequestException("The audiobook archive redirected to an untrusted host.");
                    if (isResumeRequest)
                    {
                        // A redirect can select a different mirror. Do not append until a fresh full response
                        // establishes the validator and final URL for that mirror.
                        TryDeleteFile(partialPath);
                        TryDeleteFile(manifestPath);
                        reportProgress?.Invoke(new DirectAcquisitionTransferProgress(0, null, "Restarting download", IsTransferBaseline: true));
                        resumable = false;
                        manifest = null;
                        offset = 0;
                        restartFromBeginning = true;
                        break;
                    }
                    continue;
                }

                if (isResumeRequest && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (manifest!.TotalBytes == offset && File.Exists(partialPath))
                    {
                        File.Move(partialPath, destination, overwrite: true);
                        return offset;
                    }
                    TryDeleteFile(partialPath);
                    TryDeleteFile(manifestPath);
                    reportProgress?.Invoke(new DirectAcquisitionTransferProgress(0, null, "Restarting download", IsTransferBaseline: true));
                    resumable = false;
                    manifest = null;
                    offset = 0;
                    restartFromBeginning = true;
                    break;
                }

                if (isResumeRequest && response.StatusCode is HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    // The saved mirror URL may have expired. Resolve the catalog URL again before giving up.
                    TryDeleteFile(partialPath);
                    TryDeleteFile(manifestPath);
                    reportProgress?.Invoke(new DirectAcquisitionTransferProgress(0, null, "Restarting download", IsTransferBaseline: true));
                    resumable = false;
                    manifest = null;
                    offset = 0;
                    restartFromBeginning = true;
                    break;
                }

                var append = false;
                long? totalBytes;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    var validRange = isResumeRequest && range is { Unit: "bytes" } && range.From == offset &&
                        range.Length is > 0 && range.To is { } rangeEnd && rangeEnd >= offset &&
                        response.Content.Headers.ContentLength == rangeEnd - offset + 1 &&
                        (manifest!.TotalBytes is null || manifest.TotalBytes == range.Length);
                    var changedEntityTag = manifest?.ETag is { } priorEtag && response.Headers.ETag is { } returnedEtag &&
                        (returnedEtag.IsWeak || !string.Equals(priorEtag, returnedEtag.ToString(), StringComparison.Ordinal));
                    var changedLastModified = manifest?.ETag is null && manifest?.LastModifiedUtc is { } priorModified &&
                        response.Content.Headers.LastModified is { } returnedModified && priorModified != returnedModified;
                    if (!validRange || changedEntityTag || changedLastModified)
                    {
                        TryDeleteFile(partialPath);
                        TryDeleteFile(manifestPath);
                        reportProgress?.Invoke(new DirectAcquisitionTransferProgress(0, null, "Restarting download", IsTransferBaseline: true));
                        resumable = false;
                        manifest = null;
                        offset = 0;
                        restartFromBeginning = true;
                        break;
                    }
                    append = true;
                    totalBytes = range!.Length;
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    // A 200 after If-Range means the source declined the resume or the entity changed.
                    // Its body is the complete replacement, so truncate and use this response safely.
                    append = false;
                    offset = 0;
                    totalBytes = response.Content.Headers.ContentLength;
                }
                else
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                        throw new ResumableDownloadInterruptedException(
                            $"The audiobook source returned HTTP {(int)response.StatusCode}; saved bytes were retained.");
                    response.EnsureSuccessStatusCode();
                    throw new HttpRequestException($"Unexpected HTTP status {(int)response.StatusCode} for the audiobook archive.");
                }

                if (totalBytes is > MaximumZipBytes)
                {
                    TryDeleteFile(partialPath);
                    TryDeleteFile(manifestPath);
                    throw new InvalidDataException("The audiobook archive exceeds the download-size limit.");
                }

                var returnedEtagValue = response.Headers.ETag is { IsWeak: false } responseEtag
                    ? responseEtag.ToString()
                    : null;
                var updatedManifest = new ResumeManifest(
                    requestContext?.RequestId ?? Guid.Empty,
                    requestContext?.RequestFormatId ?? Guid.Empty,
                    providerResultId,
                    uri.AbsoluteUri,
                    requestUri.AbsoluteUri,
                    returnedEtagValue,
                    response.Content.Headers.LastModified,
                    totalBytes,
                    DateTimeOffset.UtcNow);
                var startingBytes = append ? offset : 0;
                reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
                    startingBytes, totalBytes, "Downloading", IsTransferBaseline: true));
                Stream sourceStream;
                try
                {
                    sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                }
                catch (Exception exception) when (requestContext is not null && !cancellationToken.IsCancellationRequested &&
                    exception is IOException or HttpRequestException or TaskCanceledException)
                {
                    throw new ResumableDownloadInterruptedException(
                        "The audiobook archive stream could not be opened; saved bytes were retained.", exception);
                }
                await using var source = sourceStream;
                await using var target = new FileStream(
                    partialPath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                // For a full replacement, truncate the old bytes before publishing the new validator.
                // If the process stops between these operations, the zero-length partial is discarded.
                if (requestContext is not null && !append) WriteManifest(manifestPath, updatedManifest);
                long copied;
                try
                {
                    copied = await CopyBoundedAsync(
                        source,
                        target,
                        MaximumZipBytes - startingBytes,
                        cancellationToken,
                        bytes => reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
                            startingBytes + bytes, totalBytes, "Downloading")),
                        TimeSpan.FromMinutes(2));
                }
                catch (Exception exception) when (requestContext is not null && !cancellationToken.IsCancellationRequested &&
                    exception is IOException or TaskCanceledException)
                {
                    throw new ResumableDownloadInterruptedException(
                        "The audiobook archive transfer stalled or ended early; the partial file was retained.", exception);
                }
                var completeLength = checked(startingBytes + copied);
                if (totalBytes is { } expected && completeLength != expected)
                    throw new ResumableDownloadInterruptedException(
                        "The audiobook archive transfer ended before the expected byte count; saved bytes were retained.");
                if (requestContext is not null)
                {
                    WriteManifest(manifestPath, updatedManifest with { TotalBytes = totalBytes ?? completeLength, UpdatedAtUtc = DateTimeOffset.UtcNow });
                }
                File.Move(partialPath, destination, overwrite: true);
                reportProgress?.Invoke(new DirectAcquisitionTransferProgress(
                    completeLength, totalBytes ?? completeLength, "Preparing audiobook"));
                return completeLength;
                }
            }

            if (!restartFromBeginning) break;
        }

        throw new HttpRequestException("The audiobook archive could not be resumed safely after a server response mismatch.");
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken,
        Action<long>? reportProgress = null,
        TimeSpan? readIdleTimeout = null)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        using var readTimeout = readIdleTimeout is { } idleTimeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        while (true)
        {
            if (readTimeout is not null) readTimeout.CancelAfter(readIdleTimeout!.Value);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, readTimeout?.Token ?? cancellationToken);
            }
            catch (OperationCanceledException exception) when (readTimeout?.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new ResumableDownloadInterruptedException("The audiobook archive transfer was idle for too long.", exception);
            }
            if (readTimeout is not null) readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0) return copied;
            copied = checked(copied + read);
            if (copied > maximumBytes) throw new InvalidDataException("The LibriVox archive exceeded its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportProgress?.Invoke(copied);
        }
    }

    private static ResumeManifest? TryReadManifest(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<ResumeManifest>(File.ReadAllText(manifestPath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteManifest(string manifestPath, ResumeManifest manifest)
    {
        var temporaryPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest));
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateEntryPath(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is ".." or ".") ||
            normalized.Any(char.IsControl))
            throw new InvalidDataException("The LibriVox archive contains an unsafe entry path.");
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool IsAllowedRemoteUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.IdnHost.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.Equals("librivox.org", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".librivox.org", StringComparison.OrdinalIgnoreCase));

    private static Uri? SafeProjectUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && IsAllowedRemoteUri(uri) ? uri : null;

    private static string? NormalizeLanguage(string? language) => string.IsNullOrWhiteSpace(language) ? null :
        language.Trim().Equals("English", StringComparison.OrdinalIgnoreCase) ? "en" : language.Trim();

    private static string SanitizeId(string id) => new(id.Where(char.IsAsciiLetterOrDigit).Take(40).ToArray());

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record DownloadReference(string ZipUrl);

    private sealed record ResumeManifest(
        Guid RequestId,
        Guid RequestFormatId,
        string ProviderResultId,
        string OriginalUri,
        string DownloadUri,
        string? ETag,
        DateTimeOffset? LastModifiedUtc,
        long? TotalBytes,
        DateTimeOffset UpdatedAtUtc);

    private sealed class DeleteOnDisposeFileStream(string path, string? cleanupDirectory) : FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (cleanupDirectory is not null) TryDeleteDirectory(cleanupDirectory);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (cleanupDirectory is not null) TryDeleteDirectory(cleanupDirectory);
        }
    }
}
