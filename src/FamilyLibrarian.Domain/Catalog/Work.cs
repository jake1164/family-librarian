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

    public int? CatalogPageCount { get; private set; }

    public int? CatalogAudioDurationSeconds { get; private set; }

    public string? CatalogLengthSource { get; private set; }

    /// <summary>Fills previously unknown lengths only. A later source cannot overwrite an existing measurement.</summary>
    public void SetCatalogLength(int? pages, int? audioSeconds, string source, DateTimeOffset at)
    {
        if (pages is <= 0) throw new ArgumentOutOfRangeException(nameof(pages));
        if (audioSeconds is <= 0) throw new ArgumentOutOfRangeException(nameof(audioSeconds));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Trim().Length > 64) throw new ArgumentException("A length source is at most 64 characters.", nameof(source));
        if (CatalogPageCount is null && pages is not null || CatalogAudioDurationSeconds is null && audioSeconds is not null)
        {
            CatalogPageCount ??= pages;
            CatalogAudioDurationSeconds ??= audioSeconds;
            CatalogLengthSource ??= source.Trim();
            UpdatedAtUtc = at;
        }
    }

    /// <summary>
    /// The selected page count replaces the stored one. Used only when the claim that supplied the stored value is itself replaced by
    /// the same source; <see cref="SetCatalogLength"/> remains the fill-only path.
    /// </summary>
    public void ReplaceCatalogPageCount(int pages, string source, DateTimeOffset at)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        CatalogPageCount = pages;
        CatalogLengthSource = source.Trim().Length > 64 ? source.Trim()[..64] : source.Trim();
        UpdatedAtUtc = at;
    }

    /// <summary>Records the identity class of a Work being created. A saved Work's class is never reassigned.</summary>
    public void SetVersionClass(string versionClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionClass);
        if (versionClass.Trim().Length > 32)
        {
            throw new ArgumentException("A version class is at most 32 characters.", nameof(versionClass));
        }

        VersionClass = versionClass.Trim();
    }

    /// <summary>
    /// Replaces the shown first-publication date with the selected claim, with what it means and how much of it was stated. The
    /// status is recomputed by the caller from the same date, precision and clock.
    /// </summary>
    public void SetFirstPublication(
        DateOnly? date,
        DatePrecision? precision,
        DateBasis? basis,
        PublicationStatus status,
        DateTimeOffset at)
    {
        FirstPublicationDate = date;
        FirstPublicationPrecision = date is null ? null : precision;
        FirstPublicationBasis = date is null ? null : basis;
        PublicationStatus = status;
        UpdatedAtUtc = at;
    }

    /// <summary>The selected description or cover replaces the stored one (the same source changed its own statement).</summary>
    public void ReplaceDescription(string? description, DateTimeOffset at)
    {
        Description = Author.CleanOptionalText(description);
        UpdatedAtUtc = at;
    }

    public void ReplaceCoverUrl(string? coverUrl, DateTimeOffset at)
    {
        CoverUrl = Author.CleanOptionalText(coverUrl);
        UpdatedAtUtc = at;
    }

    public PublicationStatus PublicationStatus { get; private set; }

    /// <summary>The identity class of the text (Text, Abridged, Collection, ...). Null is a legacy Work whose class is unknown.</summary>
    public string? VersionClass { get; private set; }

    /// <summary>How much of <see cref="FirstPublicationDate"/> a source stated. Null is a legacy value of unknown precision.</summary>
    public DatePrecision? FirstPublicationPrecision { get; private set; }

    /// <summary>What <see cref="FirstPublicationDate"/> means. Null is a legacy value of unknown meaning.</summary>
    public DateBasis? FirstPublicationBasis { get; private set; }

    public bool IsRetired { get; private set; }

    public Guid? ReplacedById { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public ICollection<WorkAuthor> Authors { get; } = new List<WorkAuthor>();

    public ICollection<Edition> Editions { get; } = new List<Edition>();

    public ICollection<SeriesEntry> SeriesEntries { get; } = new List<SeriesEntry>();

    /// <summary>
    /// Takes the Work out of provider-reference and ISBN lookups, so the next
    /// request for the same book builds a fresh Work from current provider data.
    /// Existing requests, assets and history keep pointing at it.
    /// </summary>
    public void Retire(DateTimeOffset retiredAtUtc)
    {
        IsRetired = true;
        UpdatedAtUtc = retiredAtUtc;
    }

    public void AddAuthor(Author author, int ordinal, string? role = null, AuthorBindingBasis? bindingBasis = null)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        if (Authors.Any(workAuthor => workAuthor.AuthorId == author.Id))
        {
            return;
        }

        var credit = new WorkAuthor(this, author, ordinal, role);
        if (bindingBasis is { } basis)
        {
            credit.MarkBinding(basis);
        }

        Authors.Add(credit);
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
