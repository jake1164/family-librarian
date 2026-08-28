namespace FamilyLibrarian.Infrastructure.Gutenberg;

internal sealed class GutenbergCatalogBookEntity
{
    public long Id { get; set; }

    public Guid GenerationId { get; set; }

    public int GutenbergId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string NormalizedTitle { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public DateOnly? IssuedDate { get; set; }

    public string RightsStatus { get; set; } = string.Empty;

    public string? RightsText { get; set; }

    public int? DownloadCount { get; set; }

    public string? Summary { get; set; }

    public List<GutenbergCatalogPersonEntity> People { get; } = [];

    public List<GutenbergCatalogLanguageEntity> Languages { get; } = [];

    public List<GutenbergCatalogFormatEntity> Formats { get; } = [];
}

internal sealed class GutenbergCatalogPersonEntity
{
    public long Id { get; set; }

    public long BookId { get; set; }

    public GutenbergCatalogBookEntity Book { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public int? BirthYear { get; set; }

    public int? DeathYear { get; set; }

    public string Role { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}

internal sealed class GutenbergCatalogLanguageEntity
{
    public long Id { get; set; }

    public long BookId { get; set; }

    public GutenbergCatalogBookEntity Book { get; set; } = null!;

    public string LanguageCode { get; set; } = string.Empty;
}

internal sealed class GutenbergCatalogFormatEntity
{
    public long Id { get; set; }

    public long BookId { get; set; }

    public GutenbergCatalogBookEntity Book { get; set; } = null!;

    public string SourcePath { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public string FormatKind { get; set; } = string.Empty;

    public long? FileSizeBytes { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }
}

internal sealed class GutenbergCatalogSyncStateEntity
{
    public const string SingletonId = "gutenberg";

    public string Id { get; set; } = SingletonId;

    public Guid? ActiveGenerationId { get; set; }

    public DateTimeOffset? LastAttemptUtc { get; set; }

    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }

    public DateTimeOffset? LastSourceModifiedUtc { get; set; }

    public long? LastArchiveSizeBytes { get; set; }

    public int BookCount { get; set; }

    public int FormatCount { get; set; }

    public int ParseErrorCount { get; set; }

    public TimeSpan? LastDuration { get; set; }

    public string Status { get; set; } = "NeverSynced";

    public string? FailureMessage { get; set; }
}
