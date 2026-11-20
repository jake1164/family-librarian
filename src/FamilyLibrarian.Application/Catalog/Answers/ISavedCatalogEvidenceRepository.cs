using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Answers;

public sealed record SavedCreditEvidence(Guid AuthorId, string Name, string? Role, int Ordinal);

/// <summary>
/// A saved Work with the publication facts as the catalog stores them: the date, how much of it a source stated and what it means.
/// <see cref="Precision"/> and <see cref="Basis"/> are null for a legacy row; the answer layer says so instead of inventing an exact day
/// or an original date. The browse rows deliberately lack these, and are not reconstructed from.
/// </summary>
public sealed record SavedWorkEvidence(
    Guid WorkId,
    string Title,
    DateOnly? Date,
    DatePrecision? Precision,
    DateBasis? Basis,
    PublicationStatus Status,
    string? VersionClass,
    IReadOnlyList<SavedCreditEvidence> Credits,
    bool CreditsTruncated);

public sealed record SavedSeriesEntryEvidence(
    Guid EntryId,
    decimal? PositionSort,
    string? PositionLabel,
    bool IsPrimary,
    SeriesPositionState? PositionState,
    SavedWorkEvidence Work);

/// <summary>
/// Narrow, read-only, no-tracking evidence projections of the saved catalog for answers. Separate from the browse repository on
/// purpose: browse rows omit date precision, date basis and version class, and an answer must never reconstruct those from them.
/// Retired Works are excluded; <c>skip</c>/<c>take</c> are applied in the database.
/// </summary>
public interface ISavedCatalogEvidenceRepository
{
    /// <summary>Non-retired members of a saved series with position and publication evidence, in a stable total order.</summary>
    Task<IReadOnlyList<SavedSeriesEntryEvidence>> GetSeriesEvidenceAsync(
        Guid seriesId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Non-retired Works credited to a saved author, with each credit's role, in a stable total order.</summary>
    Task<IReadOnlyList<SavedWorkEvidence>> GetAuthorEvidenceAsync(
        Guid authorId, int skip, int take, CancellationToken cancellationToken);
}
