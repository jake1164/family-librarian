namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class OpenLibraryMetadataOptions
{
    public const string SectionName = "MetadataProviders:OpenLibrary";

    public bool Enabled { get; set; }

    // A page here is one HTTP call -- `limit` is a query parameter, not a call
    // multiplier, so raising this does not cost extra requests or strain
    // RequestsPerSecond. It only matters because BookCandidateGrouper can only
    // re-rank what actually came back in this one batch: observed live, a
    // plain single-author edition that Open Library's own relevance ranking
    // placed just past position 10 was invisible to every local sort until a
    // second network page was fetched. 25 gives the ranker enough room to find
    // the plain edition in the common case without fetching needlessly large
    // responses on every search.
    public int MaxResults { get; set; } = 25;

    public int TimeoutSeconds { get; set; } = 15;

    public int RequestsPerSecond { get; set; } = 1;

    public string? ContactEmail { get; set; }
}
