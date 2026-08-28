using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Read boundary for the locally imported Project Gutenberg RDF catalogue.
/// Provider-cache implementation details, including EF Core, stay behind this
/// interface so acquisition code never depends on persistence entities.
/// </summary>
public interface IGutenbergCatalog
{
    Task<GutenbergCatalogStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GutenbergCatalogBook>> SearchAsync(
        GutenbergCatalogSearchQuery query,
        CancellationToken cancellationToken);
}

/// <summary>Imports the daily RDF archive into a new, validated catalogue generation.</summary>
public interface IGutenbergCatalogSynchronizer
{
    Task<GutenbergCatalogSyncResult> SynchronizeAsync(CancellationToken cancellationToken);
}

public sealed record GutenbergCatalogSearchQuery(
    string Query,
    RequestMediaType MediaType,
    string? Language = null,
    bool RequireEpub = false,
    int Take = 20);

public sealed record GutenbergCatalogBook(
    int GutenbergId,
    string Title,
    string NormalizedTitle,
    string MediaType,
    string RightsStatus,
    IReadOnlyList<GutenbergCatalogPerson> People,
    IReadOnlyList<string> Languages,
    IReadOnlyList<GutenbergCatalogFormat> Formats);

public sealed record GutenbergCatalogPerson(string Name, GutenbergPersonRole Role);

public enum GutenbergPersonRole
{
    Author,
    Editor,
    Translator
}

public sealed record GutenbergCatalogFormat(
    string SourcePath,
    string MimeType,
    GutenbergFormatKind Kind,
    long? FileSizeBytes,
    DateTimeOffset? ModifiedAtUtc);

public enum GutenbergFormatKind
{
    Epub3Images,
    EpubImages,
    EpubNoImages,
    AudioMp3,
    Other
}

public sealed record GutenbergCatalogStatus(
    bool IsReady,
    DateTimeOffset? LastSuccessfulSyncUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextScheduledSyncUtc,
    int BookCount,
    int FormatCount,
    string Status,
    string? FailureMessage);

public sealed record GutenbergCatalogSyncResult(bool Succeeded, GutenbergCatalogStatus Status, string? Error = null);
