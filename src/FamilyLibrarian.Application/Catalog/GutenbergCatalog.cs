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

/// <summary>Synchronizes the locally stored Project Gutenberg catalogue.</summary>
public interface IGutenbergCatalogSynchronizer
{
    /// <summary>Builds a complete validated snapshot. Used for first import and recovery after a missed feed window.</summary>
    Task<GutenbergCatalogSyncResult> SynchronizeAsync(CancellationToken cancellationToken);

    /// <summary>Upserts only the books reported as new or updated by Project Gutenberg's daily feed.</summary>
    Task<GutenbergCatalogSyncResult> SynchronizeIncrementalAsync(CancellationToken cancellationToken);
}

/// <summary>Destructive maintenance operations for the locally stored RDF catalogue.</summary>
public interface IGutenbergCatalogMaintenance
{
    /// <summary>Deletes imported catalogue data while retaining the schema and application history.</summary>
    Task<GutenbergCatalogPurgeResult> PurgeAsync(CancellationToken cancellationToken);
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

public sealed record GutenbergCatalogPurgeResult(int DeletedBookCount);
