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

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }
}
