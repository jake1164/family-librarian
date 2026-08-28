namespace FamilyLibrarian.Infrastructure.Gutenberg;

public sealed class GutenbergCatalogOptions
{
    public const string SectionName = "GutenbergCatalog";

    public const string DefaultArchiveUrl = "https://www.gutenberg.org/cache/epub/feeds/rdf-files.tar.bz2";

    public string ArchiveUrl { get; set; } = DefaultArchiveUrl;

    public int SyncHourEastern { get; set; } = 13;

    public int BatchSize { get; set; } = 1_000;

    public int MinimumBookCount { get; set; } = 50_000;

    public int MinimumPreviousCatalogPercent { get; set; } = 95;

    /// <summary>Total attempts for one scheduled or administrator-triggered import.</summary>
    public int ImportMaxAttempts { get; set; } = 3;

    public TimeSpan ImportRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
