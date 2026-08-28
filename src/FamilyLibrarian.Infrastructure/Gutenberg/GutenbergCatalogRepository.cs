using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace FamilyLibrarian.Infrastructure.Gutenberg;

internal sealed class GutenbergCatalogRepository(AppDbContext database) : IGutenbergCatalog
{
    public async Task<GutenbergCatalogStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var state = await database.GutenbergCatalogSyncStates.AsNoTracking()
            .SingleOrDefaultAsync(state => state.Id == GutenbergCatalogSyncStateEntity.SingletonId, cancellationToken);
        return ToStatus(state, nextScheduledSyncUtc: null);
    }

    public async Task<IReadOnlyList<GutenbergCatalogBook>> SearchAsync(
        GutenbergCatalogSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var state = await database.GutenbergCatalogSyncStates.AsNoTracking()
            .SingleOrDefaultAsync(state => state.Id == GutenbergCatalogSyncStateEntity.SingletonId, cancellationToken);
        if (state?.ActiveGenerationId is not { } generationId || string.IsNullOrWhiteSpace(query.Query))
        {
            return [];
        }

        var normalizedQuery = Normalize(query.Query);
        if (string.IsNullOrEmpty(normalizedQuery))
        {
            return [];
        }

        var expectedMediaType = query.MediaType == RequestMediaType.Ebook ? "Text" : "Sound";
        var books = database.GutenbergCatalogBooks.AsNoTracking()
            .Where(book => book.GenerationId == generationId && book.MediaType == expectedMediaType)
            .Where(book => book.NormalizedTitle.Contains(normalizedQuery) ||
                book.People.Any(person => person.NormalizedName.Contains(normalizedQuery)));

        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            var language = query.Language.Trim().ToLowerInvariant();
            books = books.Where(book => book.Languages.Any(item => item.LanguageCode == language));
        }

        if (query.RequireEpub)
        {
            books = books.Where(book => book.Formats.Any(format =>
                format.FormatKind == nameof(GutenbergFormatKind.Epub3Images) ||
                format.FormatKind == nameof(GutenbergFormatKind.EpubImages) ||
                format.FormatKind == nameof(GutenbergFormatKind.EpubNoImages)));
        }

        var results = await books
            .Include(book => book.People)
            .Include(book => book.Languages)
            .Include(book => book.Formats)
            .OrderByDescending(book => book.NormalizedTitle == normalizedQuery)
            .ThenBy(book => book.NormalizedTitle)
            .Take(Math.Clamp(query.Take, 1, 100))
            .ToArrayAsync(cancellationToken);

        return results.Select(ToModel).ToArray();
    }

    internal static GutenbergCatalogStatus ToStatus(
        GutenbergCatalogSyncStateEntity? state,
        DateTimeOffset? nextScheduledSyncUtc) => new(
        state?.ActiveGenerationId is not null,
        state?.LastSuccessfulSyncUtc,
        state?.LastAttemptUtc,
        nextScheduledSyncUtc,
        state?.BookCount ?? 0,
        state?.FormatCount ?? 0,
        state?.InProgressBookCount ?? 0,
        state?.InProgressFormatCount ?? 0,
        state?.LastProgressUtc,
        state?.Status ?? "NeverSynced",
        state?.FailureMessage);

    internal static GutenbergCatalogBook ToModel(GutenbergCatalogBookEntity book) => new(
        book.GutenbergId,
        book.Title,
        book.NormalizedTitle,
        book.MediaType,
        book.RightsStatus,
        book.People.OrderBy(person => person.SortOrder).Select(person => new GutenbergCatalogPerson(
            person.Name,
            Enum.TryParse<GutenbergPersonRole>(person.Role, ignoreCase: true, out var role)
                ? role
                : GutenbergPersonRole.Author)).ToArray(),
        book.Languages.Select(language => language.LanguageCode).ToArray(),
        book.Formats.Select(format => new GutenbergCatalogFormat(
            format.SourcePath,
            format.MimeType,
            Enum.TryParse<GutenbergFormatKind>(format.FormatKind, ignoreCase: true, out var kind)
                ? kind
                : GutenbergFormatKind.Other,
            format.FileSizeBytes,
            format.ModifiedAtUtc)).ToArray());

    internal static string Normalize(string value) => new(value
        .Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
}
