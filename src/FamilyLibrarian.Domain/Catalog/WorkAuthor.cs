namespace FamilyLibrarian.Domain.Catalog;

public sealed class WorkAuthor
{
    private WorkAuthor()
    {
    }

    public WorkAuthor(Guid workId, Guid authorId, int ordinal, string? role)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        if (authorId == Guid.Empty)
        {
            throw new ArgumentException("An Author ID is required.", nameof(authorId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        WorkId = workId;
        AuthorId = authorId;
        Ordinal = ordinal;
        Role = Author.CleanOptionalText(role);
    }

    public Guid WorkId { get; private set; }

    public Work Work { get; private set; } = null!;

    public Guid AuthorId { get; private set; }

    public Author Author { get; private set; } = null!;

    public int Ordinal { get; private set; }

    public string? Role { get; private set; }
}
