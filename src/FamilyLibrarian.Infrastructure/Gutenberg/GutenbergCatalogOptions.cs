namespace FamilyLibrarian.Infrastructure.Gutenberg;

public sealed class GutenbergCatalogOptions
{
    public const string SectionName = "GutenbergCatalog";

    public const string DefaultArchiveUrl = "https://www.gutenberg.org/cache/epub/feeds/rdf-files.tar.bz2";

    public const string DefaultRecentUpdatesFeedUrl = "https://www.gutenberg.org/cache/epub/feeds/today.rss";

    public const string DefaultEbookRdfBaseUrl = "https://www.gutenberg.org/cache/epub/";

    // The public website is occasionally unavailable for days. The rsync-style
    // content mirror publishes the same /cache/epub/feeds and /cache/epub/{id}
    // paths, so it is the default fallback for every catalogue request.
    public const string DefaultFallbackArchiveUrl = "https://gutenberg.pglaf.org/cache/epub/feeds/rdf-files.tar.bz2";

    public const string DefaultFallbackRecentUpdatesFeedUrl = "https://gutenberg.pglaf.org/cache/epub/feeds/today.rss";

    public const string DefaultFallbackEbookRdfBaseUrl = "https://gutenberg.pglaf.org/cache/epub/";

    public string ArchiveUrl { get; set; } = DefaultArchiveUrl;

    public string RecentUpdatesFeedUrl { get; set; } = DefaultRecentUpdatesFeedUrl;

    public string EbookRdfBaseUrl { get; set; } = DefaultEbookRdfBaseUrl;

    // Null means "use the built-in fallback"; an explicitly configured list
    // (including an empty one) replaces it. Nullable rather than pre-filled
    // because configuration binding appends to a non-empty default list, which
    // would make the built-in fallback impossible to remove.
    public List<string>? FallbackArchiveUrls { get; set; }

    public List<string>? FallbackRecentUpdatesFeedUrls { get; set; }

    public List<string>? FallbackEbookRdfBaseUrls { get; set; }

    /// <summary>
    /// How long one source may take to start responding before the next source
    /// is tried. It does not limit the body download.
    /// </summary>
    public TimeSpan SourceResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public IReadOnlyList<string> ArchiveUrls => Combine(ArchiveUrl, FallbackArchiveUrls, DefaultFallbackArchiveUrl);

    public IReadOnlyList<string> RecentUpdatesFeedUrls =>
        Combine(RecentUpdatesFeedUrl, FallbackRecentUpdatesFeedUrls, DefaultFallbackRecentUpdatesFeedUrl);

    public IReadOnlyList<string> EbookRdfBaseUrls =>
        Combine(EbookRdfBaseUrl, FallbackEbookRdfBaseUrls, DefaultFallbackEbookRdfBaseUrl);

    private static string[] Combine(string primary, List<string>? fallbacks, string defaultFallback) =>
        [primary, .. (fallbacks ?? [defaultFallback])];

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
