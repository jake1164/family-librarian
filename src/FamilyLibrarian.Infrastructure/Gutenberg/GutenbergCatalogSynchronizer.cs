using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Gutenberg;

internal sealed partial class GutenbergCatalogSynchronizer(
    AppDbContext database,
    HttpClient httpClient,
    IOptions<GutenbergCatalogOptions> options,
    TimeProvider timeProvider,
    ILogger<GutenbergCatalogSynchronizer> logger) : IGutenbergCatalogSynchronizer
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace PgTerms = "http://www.gutenberg.org/2009/pgterms/";

    public async Task<GutenbergCatalogSyncResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var maximumAttempts = options.Value.ImportMaxAttempts;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await SynchronizeOnceAsync(cancellationToken);
            }
            catch (RetryableCatalogImportException exception) when (attempt < maximumAttempts)
            {
                database.ChangeTracker.Clear();
                var state = await GetOrCreateStateAsync(CancellationToken.None);
                state.Status = "Retrying";
                state.FailureMessage = $"Attempt {attempt} of {maximumAttempts} failed: {exception.Message} Retrying automatically.";
                await database.SaveChangesAsync(CancellationToken.None);
                LogImportRetrying(attempt, maximumAttempts, exception);
                await Task.Delay(options.Value.ImportRetryDelay, cancellationToken);
            }
            catch (RetryableCatalogImportException exception)
            {
                database.ChangeTracker.Clear();
                var state = await GetOrCreateStateAsync(CancellationToken.None);
                return new GutenbergCatalogSyncResult(false, GutenbergCatalogRepository.ToStatus(state, null), exception.Message);
            }
        }

        throw new InvalidOperationException("The Project Gutenberg catalogue import retry loop completed unexpectedly.");
    }

    public async Task<GutenbergCatalogSyncResult> SynchronizeIncrementalAsync(CancellationToken cancellationToken)
    {
        var state = await GetOrCreateStateAsync(cancellationToken);
        if (RequiresFullReconciliation(state))
        {
            return await SynchronizeAsync(cancellationToken);
        }

        var startedAt = timeProvider.GetUtcNow();
        state.LastAttemptUtc = startedAt;
        state.Status = "CheckingUpdates";
        state.FailureMessage = null;
        ResetImportProgress(state, startedAt);
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            var updates = await DownloadRecentUpdatesAsync(cancellationToken);
            var generationId = state.ActiveGenerationId!.Value;
            var books = new List<GutenbergCatalogBookEntity>(updates.Count);
            foreach (var gutenbergId in updates)
            {
                using var response = await httpClient.GetAsync(
                    CreateRdfUri(gutenbergId),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var rdf = await response.Content.ReadAsStreamAsync(cancellationToken);
                var book = ParseBook(rdf, generationId);
                if (book is null || book.GutenbergId != gutenbergId)
                {
                    throw new InvalidDataException($"The RDF record for Project Gutenberg eBook #{gutenbergId} was invalid.");
                }

                books.Add(book);
            }

            state.Status = "Importing";
            state.InProgressBookCount = books.Count;
            state.InProgressFormatCount = books.Sum(book => book.Formats.Count);
            state.LastProgressUtc = timeProvider.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            foreach (var book in books)
            {
                var existing = await database.GutenbergCatalogBooks
                    .Include(item => item.People)
                    .Include(item => item.Languages)
                    .Include(item => item.Formats)
                    .SingleOrDefaultAsync(
                        item => item.GenerationId == generationId && item.GutenbergId == book.GutenbergId,
                        cancellationToken);
                if (existing is null)
                {
                    database.GutenbergCatalogBooks.Add(book);
                }
                else if (!string.Equals(existing.SourceFingerprint, book.SourceFingerprint, StringComparison.Ordinal))
                {
                    ApplyUpdate(existing, book);
                }
            }

            await database.SaveChangesAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            state.LastSuccessfulIncrementalSyncUtc = now;
            state.BookCount = await database.GutenbergCatalogBooks.CountAsync(
                item => item.GenerationId == generationId,
                cancellationToken);
            state.FormatCount = await database.GutenbergCatalogFormats.CountAsync(
                item => item.Book.GenerationId == generationId,
                cancellationToken);
            ResetImportProgress(state, timeProvider.GetUtcNow());
            state.LastDuration = now - startedAt;
            state.Status = "Completed";
            state.FailureMessage = null;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            LogIncrementalSyncCompleted(updates.Count, state.BookCount, state.FormatCount, state.LastDuration);
            return new GutenbergCatalogSyncResult(true, GutenbergCatalogRepository.ToStatus(state, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogIncrementalSyncFailed(exception);
            database.ChangeTracker.Clear();
            var failedState = await GetOrCreateStateAsync(CancellationToken.None);
            failedState.Status = "Failed";
            failedState.FailureMessage = exception.Message;
            failedState.LastDuration = timeProvider.GetUtcNow() - startedAt;
            await database.SaveChangesAsync(CancellationToken.None);
            return new GutenbergCatalogSyncResult(false, GutenbergCatalogRepository.ToStatus(failedState, null), exception.Message);
        }
    }

    private async Task<GutenbergCatalogSyncResult> SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var state = await GetOrCreateStateAsync(cancellationToken);
        await RemoveUnpublishedGenerationsAsync(state.ActiveGenerationId, cancellationToken);
        state.LastAttemptUtc = startedAt;
        state.Status = "Downloading";
        state.FailureMessage = null;
        ResetImportProgress(state, startedAt);
        await database.SaveChangesAsync(cancellationToken);

        var generationId = Guid.NewGuid();
        var bookCount = 0;
        var formatCount = 0;
        var parseErrorCount = 0;
        try
        {
            using var response = await httpClient.GetAsync(
                options.Value.ArchiveUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            state.LastArchiveSizeBytes = response.Content.Headers.ContentLength;
            state.LastSourceModifiedUtc = response.Content.Headers.LastModified;
            state.LastProgressUtc = timeProvider.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);

            await using var archive = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var decompressor = StartDecompressor();
            var temporaryTarPath = Path.Combine(Path.GetTempPath(), $"family-librarian-gutenberg-{Guid.NewGuid():N}.tar");
            try
            {
                var copyArchive = CopyArchiveToDecompressorAsync(archive, decompressor, cancellationToken);
                var readDecompressorError = decompressor.StandardError.ReadToEndAsync(cancellationToken);
                await using var temporaryTar = new FileStream(
                    temporaryTarPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await decompressor.StandardOutput.BaseStream.CopyToAsync(temporaryTar, cancellationToken);
                await copyArchive;
                await decompressor.WaitForExitAsync(cancellationToken);
                var decompressorError = await readDecompressorError;
                if (decompressor.ExitCode != 0)
                {
                    throw new InvalidDataException($"The bzip2 decoder failed: {decompressorError.Trim()}");
                }

                state.Status = "Parsing";
                state.LastProgressUtc = timeProvider.GetUtcNow();
                await database.SaveChangesAsync(cancellationToken);
                temporaryTar.Position = 0;
                using var tar = new TarReader(temporaryTar, leaveOpen: true);
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                        entry.DataStream is null ||
                        !entry.Name.EndsWith(".rdf", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var book = ParseBook(entry.DataStream, generationId);
                        if (book is null)
                        {
                            continue;
                        }

                        formatCount += book.Formats.Count;
                        database.GutenbergCatalogBooks.Add(book);
                        bookCount++;
                    }
                    catch (Exception exception) when (exception is XmlException or FormatException or InvalidDataException)
                    {
                        parseErrorCount++;
                    }

                    if (bookCount > 0 && bookCount % options.Value.BatchSize == 0)
                    {
                        state.Status = "Importing";
                        state.InProgressBookCount = bookCount;
                        state.InProgressFormatCount = formatCount;
                        state.ParseErrorCount = parseErrorCount;
                        state.LastProgressUtc = timeProvider.GetUtcNow();
                        await database.SaveChangesAsync(cancellationToken);
                        database.ChangeTracker.Clear();
                        state = await GetOrCreateStateAsync(cancellationToken);
                    }
                }
            }
            finally
            {
                if (!decompressor.HasExited)
                {
                    decompressor.Kill(entireProcessTree: true);
                }

                File.Delete(temporaryTarPath);
            }

            await database.SaveChangesAsync(cancellationToken);
            state = await GetOrCreateStateAsync(cancellationToken);
            ValidateImportedGeneration(state, bookCount, formatCount, parseErrorCount);

            state.ActiveGenerationId = generationId;
            state.LastSuccessfulSyncUtc = timeProvider.GetUtcNow();
            state.BookCount = bookCount;
            state.FormatCount = formatCount;
            ResetImportProgress(state, timeProvider.GetUtcNow());
            state.ParseErrorCount = parseErrorCount;
            state.LastDuration = timeProvider.GetUtcNow() - startedAt;
            state.Status = "Completed";
            state.FailureMessage = null;
            await database.SaveChangesAsync(cancellationToken);

            LogImportCompleted(bookCount, formatCount, parseErrorCount, state.LastDuration);
            return new GutenbergCatalogSyncResult(true, GutenbergCatalogRepository.ToStatus(state, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RemoveImportedGenerationAsync(generationId, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            LogImportFailed(exception);
            database.ChangeTracker.Clear();
            await RemoveImportedGenerationAsync(generationId, CancellationToken.None);
            var failedState = await GetOrCreateStateAsync(CancellationToken.None);
            failedState.Status = "Failed";
            failedState.FailureMessage = exception.Message;
            failedState.ParseErrorCount = parseErrorCount;
            failedState.LastDuration = timeProvider.GetUtcNow() - startedAt;
            await database.SaveChangesAsync(CancellationToken.None);
            if (IsRetryableImportFailure(exception))
            {
                throw new RetryableCatalogImportException(exception);
            }

            return new GutenbergCatalogSyncResult(false, GutenbergCatalogRepository.ToStatus(failedState, null), exception.Message);
        }
    }

    private async Task RemoveImportedGenerationAsync(Guid generationId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await database.GutenbergCatalogBooks
            .Where(book => book.GenerationId == generationId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static void ResetImportProgress(GutenbergCatalogSyncStateEntity state, DateTimeOffset progressedAt)
    {
        state.InProgressBookCount = 0;
        state.InProgressFormatCount = 0;
        state.LastProgressUtc = progressedAt;
    }

    private bool RequiresFullReconciliation(GutenbergCatalogSyncStateEntity state)
    {
        if (state.ActiveGenerationId is null)
        {
            return true;
        }

        var lastSuccessfulCheck = state.LastSuccessfulIncrementalSyncUtc ?? state.LastSuccessfulSyncUtc;
        return lastSuccessfulCheck is null ||
            timeProvider.GetUtcNow() - lastSuccessfulCheck > TimeSpan.FromHours(options.Value.MaximumIncrementalGapHours);
    }

    private async Task<IReadOnlyList<int>> DownloadRecentUpdatesAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(options.Value.RecentUpdatesFeedUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        return document.Descendants("item")
            .Select(item => item.Element("link")?.Value.Trim())
            .Select(TryGetGutenbergIdFromLink)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
    }

    private Uri CreateRdfUri(int gutenbergId) => new(
        new Uri(options.Value.EbookRdfBaseUrl, UriKind.Absolute),
        $"{gutenbergId}/pg{gutenbergId}.rdf");

    private static int? TryGetGutenbergIdFromLink(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.Segments.Select(segment => segment.Trim('/')).Where(segment => segment.Length > 0).ToArray();
        return segments.Length >= 2 &&
            string.Equals(segments[^2], "ebooks", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[^1], CultureInfo.InvariantCulture, out var gutenbergId)
            ? gutenbergId
            : null;
    }

    private static void ApplyUpdate(GutenbergCatalogBookEntity target, GutenbergCatalogBookEntity source)
    {
        target.SourceFingerprint = source.SourceFingerprint;
        target.Title = source.Title;
        target.NormalizedTitle = source.NormalizedTitle;
        target.MediaType = source.MediaType;
        target.IssuedDate = source.IssuedDate;
        target.RightsStatus = source.RightsStatus;
        target.RightsText = source.RightsText;
        target.DownloadCount = source.DownloadCount;
        target.Summary = source.Summary;
        target.People.Clear();
        target.Languages.Clear();
        target.Formats.Clear();
        target.People.AddRange(source.People);
        target.Languages.AddRange(source.Languages);
        target.Formats.AddRange(source.Formats);
    }

    private async Task RemoveUnpublishedGenerationsAsync(Guid? activeGenerationId, CancellationToken cancellationToken)
    {
        var unpublished = activeGenerationId is { } activeGenerationIdValue
            ? database.GutenbergCatalogBooks.Where(book => book.GenerationId != activeGenerationIdValue)
            : database.GutenbergCatalogBooks;
        await unpublished.ExecuteDeleteAsync(cancellationToken);
    }

    private static Process StartDecompressor()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bzip2",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--decompress");
        startInfo.ArgumentList.Add("--stdout");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the bzip2 decoder.");
    }

    private static async Task CopyArchiveToDecompressorAsync(
        Stream archive,
        Process decompressor,
        CancellationToken cancellationToken)
    {
        await using var input = decompressor.StandardInput.BaseStream;
        await archive.CopyToAsync(input, cancellationToken);
    }

    private static bool IsRetryableImportFailure(Exception exception) =>
        exception is HttpRequestException or IOException;

    private sealed class RetryableCatalogImportException(Exception innerException)
        : Exception(innerException.Message, innerException);

    private async Task<GutenbergCatalogSyncStateEntity> GetOrCreateStateAsync(CancellationToken cancellationToken)
    {
        var state = await database.GutenbergCatalogSyncStates
            .SingleOrDefaultAsync(item => item.Id == GutenbergCatalogSyncStateEntity.SingletonId, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = new GutenbergCatalogSyncStateEntity();
        database.GutenbergCatalogSyncStates.Add(state);
        return state;
    }

    private void ValidateImportedGeneration(
        GutenbergCatalogSyncStateEntity previous,
        int bookCount,
        int formatCount,
        int parseErrorCount)
    {
        if (bookCount < options.Value.MinimumBookCount)
        {
            throw new InvalidDataException($"The RDF catalogue contained only {bookCount} books.");
        }

        if (previous.BookCount > 0 && bookCount * 100L < previous.BookCount * options.Value.MinimumPreviousCatalogPercent)
        {
            throw new InvalidDataException("The imported RDF catalogue is unexpectedly smaller than the active catalogue.");
        }

        if (formatCount == 0)
        {
            throw new InvalidDataException("The RDF catalogue contained no file formats.");
        }

        if (parseErrorCount * 100L > Math.Max(bookCount, 1L) * 5L)
        {
            throw new InvalidDataException("Too many RDF records could not be parsed.");
        }
    }

    private static GutenbergCatalogBookEntity? ParseBook(Stream stream, Guid generationId)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var ebook = document.Descendants(PgTerms + "ebook").SingleOrDefault();
        if (ebook is null || !TryGetGutenbergId(ebook, out var gutenbergId))
        {
            return null;
        }

        var title = Value(ebook, DcTerms + "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var rights = Value(ebook, DcTerms + "rights");
        var book = new GutenbergCatalogBookEntity
        {
            GenerationId = generationId,
            GutenbergId = gutenbergId,
            SourceFingerprint = ComputeFingerprint(document),
            Title = title,
            NormalizedTitle = GutenbergCatalogRepository.Normalize(title),
            MediaType = Value(ebook, DcTerms + "type", Rdf + "value") ?? "Unknown",
            IssuedDate = ParseDate(Value(ebook, DcTerms + "issued")),
            RightsText = rights,
            RightsStatus = ClassifyRights(rights),
            DownloadCount = ParseInt(Value(ebook, PgTerms + "downloads")),
            Summary = Value(ebook, PgTerms + "marc520")
        };

        AddPeople(book, ebook, DcTerms + "creator", GutenbergPersonRole.Author);
        AddPeople(book, ebook, DcTerms + "contributor", GutenbergPersonRole.Editor);
        AddPeople(book, ebook, DcTerms + "translator", GutenbergPersonRole.Translator);
        foreach (var language in ebook.Elements(DcTerms + "language")
                     .Select(element => Value(element, Rdf + "Description", Rdf + "value") ??
                                        element.Descendants(Rdf + "value").FirstOrDefault()?.Value)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value!.Trim().ToLowerInvariant())
                     .Distinct(StringComparer.Ordinal))
        {
            book.Languages.Add(new GutenbergCatalogLanguageEntity { LanguageCode = language });
        }

        foreach (var file in document.Descendants(PgTerms + "file"))
        {
            var source = file.Attribute(Rdf + "about")?.Value;
            if (!TryGetSourcePath(source, out var sourcePath))
            {
                continue;
            }

            var mime = file.Descendants(DcTerms + "format").Descendants(Rdf + "value")
                .Select(value => value.Value.Trim()).FirstOrDefault() ?? "application/octet-stream";
            book.Formats.Add(new GutenbergCatalogFormatEntity
            {
                SourcePath = sourcePath,
                MimeType = mime,
                FormatKind = ClassifyFormat(sourcePath, mime).ToString(),
                FileSizeBytes = ParseLong(Value(file, DcTerms + "extent")),
                ModifiedAtUtc = ParseDateTime(Value(file, DcTerms + "modified"))
            });
        }

        return book;
    }

    private static string ComputeFingerprint(XDocument document) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))));

    private static void AddPeople(GutenbergCatalogBookEntity book, XElement ebook, XName relation, GutenbergPersonRole role)
    {
        foreach (var relationElement in ebook.Elements(relation))
        {
            var agent = relationElement.Descendants(PgTerms + "agent").FirstOrDefault();
            var name = agent is null ? null : Value(agent, PgTerms + "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            book.People.Add(new GutenbergCatalogPersonEntity
            {
                Name = name,
                NormalizedName = GutenbergCatalogRepository.Normalize(name),
                BirthYear = ParseInt(Value(agent!, PgTerms + "birthdate")),
                DeathYear = ParseInt(Value(agent!, PgTerms + "deathdate")),
                Role = role.ToString(),
                SortOrder = book.People.Count
            });
        }
    }

    private static bool TryGetGutenbergId(XElement ebook, out int gutenbergId)
    {
        gutenbergId = 0;
        var about = ebook.Attribute(Rdf + "about")?.Value;
        return about is not null && int.TryParse(about.Split('/').Last(), CultureInfo.InvariantCulture, out gutenbergId);
    }

    private static string? Value(XElement element, XName child) => element.Element(child)?.Value.Trim();

    private static string? Value(XElement element, XName child, XName descendant) =>
        element.Element(child)?.Descendants(descendant).FirstOrDefault()?.Value.Trim();

    private static DateOnly? ParseDate(string? value) => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static DateTimeOffset? ParseDateTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static int? ParseInt(string? value) => int.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static long? ParseLong(string? value) => long.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string ClassifyRights(string? rights) =>
        rights?.Contains("public domain", StringComparison.OrdinalIgnoreCase) == true ? "PublicDomainUS" :
        rights?.Contains("copyright", StringComparison.OrdinalIgnoreCase) == true ? "Copyrighted" : "Unknown";

    private static GutenbergFormatKind ClassifyFormat(string path, string mimeType) =>
        mimeType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase) ? GutenbergFormatKind.AudioMp3 :
        path.EndsWith(".epub3.images", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-images-3.epub", StringComparison.OrdinalIgnoreCase) ? GutenbergFormatKind.Epub3Images :
        path.EndsWith(".epub.images", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-images.epub", StringComparison.OrdinalIgnoreCase) ? GutenbergFormatKind.EpubImages :
        mimeType.Equals("application/epub+zip", StringComparison.OrdinalIgnoreCase) ? GutenbergFormatKind.EpubNoImages :
        GutenbergFormatKind.Other;

    private static bool TryGetSourcePath(string? source, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        path = uri.AbsolutePath;
        return path.Length > 1;
    }

    [LoggerMessage(EventId = 901, Level = LogLevel.Information, Message = "gutenberg.catalog.import.completed: {BookCount} books, {FormatCount} formats, {ParseErrorCount} parse errors in {Duration}.")]
    private partial void LogImportCompleted(int bookCount, int formatCount, int parseErrorCount, TimeSpan? duration);

    [LoggerMessage(EventId = 902, Level = LogLevel.Warning, Message = "gutenberg.catalog.import.failed")]
    private partial void LogImportFailed(Exception exception);

    [LoggerMessage(EventId = 905, Level = LogLevel.Information, Message = "gutenberg.catalog.incremental.completed: {UpdatedFeedEntries} feed entries checked; {BookCount} books and {FormatCount} formats are locally searchable in {Duration}.")]
    private partial void LogIncrementalSyncCompleted(int updatedFeedEntries, int bookCount, int formatCount, TimeSpan? duration);

    [LoggerMessage(EventId = 906, Level = LogLevel.Warning, Message = "gutenberg.catalog.incremental.failed")]
    private partial void LogIncrementalSyncFailed(Exception exception);

    [LoggerMessage(EventId = 904, Level = LogLevel.Warning, Message = "gutenberg.catalog.import.retrying: attempt {Attempt} of {MaximumAttempts}.")]
    private partial void LogImportRetrying(int attempt, int maximumAttempts, Exception exception);
}
