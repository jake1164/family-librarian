using System.IO.Compression;
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
    ManualImportPolicy importPolicy) : IAutomaticDirectAcquisitionProvider
{
    private const long MaximumZipBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> HarmlessMetadataExtensions = [".txt", ".jpg", ".jpeg", ".pdf"];
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

    public async Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
        FulfillmentOption fulfillmentOption, CancellationToken cancellationToken)
    {
        if (fulfillmentOption.MediaType != RequestMediaType.Audiobook)
            throw new InvalidOperationException("LibriVox provides audiobook files only.");
        var reference = JsonSerializer.Deserialize<DownloadReference>(fulfillmentOption.ProviderData ?? string.Empty)
            ?? throw new InvalidOperationException("This LibriVox option has no download reference.");
        if (!Uri.TryCreate(reference.ZipUrl, UriKind.Absolute, out var uri) || !IsAllowedRemoteUri(uri))
            throw new InvalidOperationException("The LibriVox download address is invalid.");

        var directory = Path.Combine(Path.GetTempPath(), $"family-librarian-librivox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, "recording.zip");
        var extracted = new List<string>();
        try
        {
            await DownloadArchiveAsync(uri, archivePath, cancellationToken);
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
                var outputPath = Path.Combine(directory, $"track-{index + 1:000}.mp3");
                await using var source = audioEntries[index].Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyBoundedAsync(source, output, audioEntries[index].Length, cancellationToken);
                extracted.Add(outputPath);
            }
        }
        catch (Exception exception)
        {
            TryDeleteDirectory(directory);
            if (exception is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
                throw new InvalidOperationException("The LibriVox archive failed safety checks or could not be processed.", exception);
            throw;
        }

        File.Delete(archivePath);
        return extracted.Select((path, index) => new DirectAcquisitionFile(
            new DeleteOnDisposeFileStream(path, extracted.Count == index + 1 ? directory : null),
            $"librivox-{SanitizeId(fulfillmentOption.ProviderResultId)}-{index + 1:000}.mp3")).ToArray();
    }

    private async Task DownloadArchiveAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var response = await downloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location ?? throw new HttpRequestException("The LibriVox archive redirect had no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsAllowedRemoteUri(uri)) throw new HttpRequestException("The LibriVox archive redirected to an untrusted host.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumZipBytes)
                throw new InvalidDataException("The LibriVox archive exceeds the download-size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyBoundedAsync(source, target, MaximumZipBytes, cancellationToken);
            return;
        }
        throw new HttpRequestException("The LibriVox archive exceeded the redirect limit.");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            copied = checked(copied + read);
            if (copied > maximumBytes) throw new InvalidDataException("The LibriVox archive exceeded its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
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
