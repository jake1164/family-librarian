namespace FamilyLibrarian.Domain.Catalog;

public sealed class SeriesEntry
{
    private SeriesEntry()
    {
    }

    public SeriesEntry(
        Series series,
        Work work,
        string? positionLabel,
        decimal? positionSort,
        bool isPrimary,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(work);

        SeriesId = series.Id;
        Series = series;
        WorkId = work.Id;
        Work = work;
        PositionLabel = Author.CleanOptionalText(positionLabel);
        PositionSort = positionSort;
        IsPrimary = isPrimary;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid SeriesId { get; private set; }

    public Series Series { get; private set; } = null!;

    public Guid WorkId { get; private set; }

    public Work Work { get; private set; } = null!;

    public string? PositionLabel { get; private set; }

    public decimal? PositionSort { get; private set; }

    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }
}
