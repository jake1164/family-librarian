namespace FamilyLibrarian.Infrastructure.Gutenberg;

public sealed class GutenbergCatalogOptions
{
    public const string SectionName = "GutenbergCatalog";

    public const string DefaultArchiveUrl = "https://www.gutenberg.org/cache/epub/feeds/rdf-files.tar.bz2";

    public const string DefaultRecentUpdatesFeedUrl = "https://www.gutenberg.org/cache/epub/feeds/today.rss";

    public const string DefaultEbookRdfBaseUrl = "https://www.gutenberg.org/cache/epub/";

    public string ArchiveUrl { get; set; } = DefaultArchiveUrl;

    public string RecentUpdatesFeedUrl { get; set; } = DefaultRecentUpdatesFeedUrl;

    public string EbookRdfBaseUrl { get; set; } = DefaultEbookRdfBaseUrl;

    public int SyncHourEastern { get; set; } = 13;

    // A committed batch also provides the durable progress update shown to
    // administrators. Keep this small enough that a first import becomes
    // observable promptly without making every RDF record its own transaction.
    public int BatchSize { get; set; } = 250;

    public int MinimumBookCount { get; set; } = 50_000;

    public int MinimumPreviousCatalogPercent { get; set; } = 95;

    /// <summary>Total attempts for one scheduled or administrator-triggered import.</summary>
    public int ImportMaxAttempts { get; set; } = 3;

    public TimeSpan ImportRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The new/updated-books feed covers only roughly one day. A longer gap
    /// requires a complete reconciliation so no updates are silently missed.
    /// </summary>
    public int MaximumIncrementalGapHours { get; set; } = 36;
}
