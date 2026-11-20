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

    /// <summary>Why the entry is bound to the Series. Null is a legacy entry nobody classified.</summary>
    public SeriesBindingBasis? BindingBasis { get; private set; }

    /// <summary>Whether sources agree on the position. Null is a legacy position nobody attributed.</summary>
    public SeriesPositionState? PositionState { get; private set; }

    /// <summary>Records why the entry is bound to the Series. Set once, when the entry is created.</summary>
    public void MarkBinding(SeriesBindingBasis basis) => BindingBasis ??= basis;

    /// <summary>
    /// The selected position replaces the shown one, with whether sources agree. Only the claim-selection path calls this, so a
    /// legacy entry keeps its stored position until a source actually states one.
    /// </summary>
    public void ApplyPosition(string? positionLabel, decimal? positionSort, SeriesPositionState state, DateTimeOffset at)
    {
        PositionLabel = Author.CleanOptionalText(positionLabel);
        PositionSort = positionSort;
        PositionState = state;
        UpdatedAtUtc = at;
    }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }
}
