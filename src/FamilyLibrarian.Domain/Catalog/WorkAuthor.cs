namespace FamilyLibrarian.Domain.Catalog;

public sealed class WorkAuthor
{
    private WorkAuthor()
    {
    }

    public WorkAuthor(Work work, Author author, int ordinal, string? role)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        WorkId = work.Id;
        Work = work;
        AuthorId = author.Id;
        Author = author;
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
