namespace FamilyLibrarian.Domain.Catalog;

public sealed class Series
{
    private Series()
    {
    }

    public Series(string name, SeriesStatus status, DateTimeOffset createdAtUtc)
    {
        Name = Author.RequireText(name, nameof(name));
        NormalizedName = CatalogText.NormalizeForMatch(Name);
        Status = status;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Name { get; private set; } = null!;

    public string NormalizedName { get; private set; } = null!;

    public string? Description { get; private set; }

    public SeriesStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public ICollection<SeriesEntry> Entries { get; } = new List<SeriesEntry>();

    // One-directional on purpose: a provider that has since stopped reporting
    // the series as complete does not un-complete it here. Nothing in this
    // codebase supplies a signal for "reopened", so there is nothing correct
    // to do with one yet.
    public void MarkCompleted(DateTimeOffset updatedAtUtc)
    {
        if (Status == SeriesStatus.Completed)
        {
            return;
        }

        Status = SeriesStatus.Completed;
        UpdatedAtUtc = updatedAtUtc;
    }
}
