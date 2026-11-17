namespace FamilyLibrarian.Domain.Catalog;

public sealed class Edition
{
    private Edition()
    {
    }

    public Edition(
        Guid workId,
        string title,
        EditionFormat format,
        string? isbn13,
        DateOnly? publicationDate,
        DateTimeOffset createdAtUtc)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        WorkId = workId;
        Title = Author.RequireText(title, nameof(title));
        Format = format;
        Isbn13 = Author.CleanOptionalText(isbn13);
        PublicationDate = publicationDate;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid WorkId { get; private set; }

    public Work Work { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string? Publisher { get; private set; }

    public string? Language { get; private set; }

    public DateOnly? PublicationDate { get; private set; }

    public EditionFormat Format { get; private set; }

    public string? Isbn13 { get; private set; }

    /// <summary>How the Edition came to belong to the Work. Null is a legacy row; informational, not a safety gate.</summary>
    public EditionIdentityBasis? IdentityBasis { get; private set; }

    /// <summary>The provider id (historical text, not a reference) that supplied the Edition. Null for legacy rows.</summary>
    public string? SourceProviderId { get; private set; }

    /// <summary>How much of <see cref="PublicationDate"/> the source stated. Null is a legacy value of unknown precision.</summary>
    public DatePrecision? PublicationPrecision { get; private set; }

    /// <summary>
    /// Records what the source stated about the edition when it is written: its language and publisher (never guessed, so null
    /// stays null), how it came to belong to the Work, which provider supplied it, and how much of its date was stated.
    /// </summary>
    public void ApplyObservation(
        string? language,
        string? publisher,
        EditionIdentityBasis identityBasis,
        string sourceProviderId,
        DatePrecision? publicationPrecision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProviderId);
        Language = Author.CleanOptionalText(language);
        Publisher = Author.CleanOptionalText(publisher);
        IdentityBasis = identityBasis;
        SourceProviderId = sourceProviderId.Trim();
        PublicationPrecision = PublicationDate is null ? null : publicationPrecision ?? DatePrecision.Day;
    }

    /// <summary>The selected publication date of this edition replaces the stored one.</summary>
    public void SetPublicationDate(DateOnly? date, DatePrecision? precision, DateTimeOffset at)
    {
        PublicationDate = date;
        PublicationPrecision = date is null ? null : precision ?? DatePrecision.Day;
        UpdatedAtUtc = at;
    }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }
}
