namespace FamilyLibrarian.Domain.Catalog;

public sealed class Author
{
    private Author()
    {
    }

    public Author(string canonicalName, string? sortName, DateTimeOffset createdAtUtc)
    {
        CanonicalName = RequireText(canonicalName, nameof(canonicalName));
        NormalizedName = CatalogText.NormalizeForMatch(CanonicalName);
        SortName = CleanOptionalText(sortName);
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string CanonicalName { get; private set; } = null!;

    public string NormalizedName { get; private set; } = null!;

    public string? SortName { get; private set; }

    public string? Biography { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public ICollection<WorkAuthor> WorkAuthors { get; } = new List<WorkAuthor>();

    internal static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    internal static string? CleanOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
