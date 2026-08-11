namespace FamilyLibrarian.Domain.Catalog;

public sealed class Work
{
    private Work()
    {
    }

    public Work(
        string canonicalTitle,
        string? description,
        string? coverUrl,
        DateOnly? firstPublicationDate,
        PublicationStatus publicationStatus,
        DateTimeOffset createdAtUtc)
    {
        CanonicalTitle = Author.RequireText(canonicalTitle, nameof(canonicalTitle));
        NormalizedTitle = CatalogText.NormalizeForMatch(CanonicalTitle);
        Description = Author.CleanOptionalText(description);
        CoverUrl = Author.CleanOptionalText(coverUrl);
        FirstPublicationDate = firstPublicationDate;
        PublicationStatus = publicationStatus;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string CanonicalTitle { get; private set; } = null!;

    public string NormalizedTitle { get; private set; } = null!;

    public string? Description { get; private set; }

    public string? CoverUrl { get; private set; }

    public DateOnly? FirstPublicationDate { get; private set; }

    public PublicationStatus PublicationStatus { get; private set; }

    public bool IsRetired { get; private set; }

    public Guid? ReplacedById { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public ICollection<WorkAuthor> Authors { get; } = new List<WorkAuthor>();

    public ICollection<Edition> Editions { get; } = new List<Edition>();

    public ICollection<SeriesEntry> SeriesEntries { get; } = new List<SeriesEntry>();

    public void AddAuthor(Author author, int ordinal, string? role = null)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        if (Authors.Any(workAuthor => workAuthor.AuthorId == author.Id))
        {
            return;
        }

        Authors.Add(new WorkAuthor(this, author, ordinal, role));
    }

    public void AddEdition(Edition edition)
    {
        ArgumentNullException.ThrowIfNull(edition);
        if (edition.WorkId != Id)
        {
            throw new ArgumentException("The edition belongs to a different Work.", nameof(edition));
        }

        if (edition.Isbn13 is not null && Editions.Any(item => item.Isbn13 == edition.Isbn13))
        {
            return;
        }

        Editions.Add(edition);
    }

    public void AddSeriesEntry(SeriesEntry seriesEntry)
    {
        ArgumentNullException.ThrowIfNull(seriesEntry);
        if (seriesEntry.WorkId != Id)
        {
            throw new ArgumentException("The series entry belongs to a different Work.", nameof(seriesEntry));
        }

        if (SeriesEntries.Any(item => item.SeriesId == seriesEntry.SeriesId))
        {
            return;
        }

        SeriesEntries.Add(seriesEntry);
    }
}
