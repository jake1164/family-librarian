using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

public sealed record BookCandidate(
    string ProviderId,
    string ProviderName,
    string ExternalId,
    string Title,
    IReadOnlyList<string> Authors,
    string? Description,
    string? CoverUrl,
    DateOnly? PublicationDate,
    IReadOnlyList<BookEditionCandidate> Editions,
    IReadOnlyList<BookSeriesCandidate> Series,
    string? Publisher = null,
    int? PageCount = null,
    IReadOnlyList<string>? Subjects = null,
    string? SourceUrl = null,
    string? Language = null,
    IReadOnlyList<BookCandidateSource>? MergedSources = null,
    string? WorkTitle = null,
    BookCandidateVersion? AssessedVersion = null)
{
    public IReadOnlyList<string> Subjects { get; init; } = Subjects ?? [];

    // Populated only by BookCandidateGrouper when this candidate is chosen to
    // represent a group of matching candidates from different providers --
    // every provider's own copy of this record has an empty list here.
    public IReadOnlyList<BookCandidateSource> MergedSources { get; init; } = MergedSources ?? [];

    /// <summary>
    /// How much of <see cref="PublicationDate"/> the source stated. A year-only or year-month value is carried as the first day of
    /// that interval, so this is what keeps a reader from mistaking it for an exact day. Null means a full date (the historical
    /// meaning of the date) or no date at all.
    /// </summary>
    public DatePrecision? PublicationDatePrecision { get; init; }

    /// <summary>
    /// What <see cref="PublicationDate"/> means for the Work: the original first publication, a date derived from one edition, or
    /// unverified. Null is unknown; it is never read as the original date.
    /// </summary>
    public DateBasis? PublicationDateBasis { get; init; }

    /// <summary>
    /// Optional per-credit detail aligned by name with <see cref="Authors"/>: the credit's role and the provider's own author id,
    /// where the provider supplies them. A provider that cannot leaves this empty; nothing is guessed.
    /// </summary>
    public IReadOnlyList<BookAuthorCredit> AuthorCredits { get; init; } = [];
}

/// <summary>
/// One catalog source (a provider + its own record for the same work) that
/// was merged into a <see cref="BookCandidate"/> during search-result
/// grouping, so the UI can still link out to every source rather than just
/// the one chosen to represent the group.
/// </summary>
public sealed record BookCandidateSource(
    string ProviderId,
    string ProviderName,
    string ExternalId,
    string? SourceUrl);

public sealed record BookEditionCandidate(
    string Title,
    string? Isbn13,
    string Format,
    DateOnly? PublicationDate,
    string? Language = null,
    string? Publisher = null)
{
    /// <summary>How much of <see cref="PublicationDate"/> the source stated; null is a full date.</summary>
    public DatePrecision? PublicationDatePrecision { get; init; }
}

public sealed record BookSeriesCandidate(
    string Name,
    string? PositionLabel,
    bool IsPrimary,
    decimal? PositionSort = null,
    bool IsCompleted = false)
{
    /// <summary>The provider's own id for the series, when it supplies one. With it an ambiguous name can still bind exactly.</summary>
    public string? SourceSeriesId { get; init; }

    /// <summary>The ordering the source's label states; null reads the label for an explicit qualifier, never assuming publication order.</summary>
    public SeriesOrderingKind? OrderingKind { get; init; }
}

/// <param name="Name">The credited name exactly as the provider reported it.</param>
/// <param name="Role">The credit's role (author, illustrator, narrator, ...) when the provider states one.</param>
/// <param name="SourceAuthorId">The provider's own id for the person, when it supplies one.</param>
public sealed record BookAuthorCredit(string Name, string? Role = null, string? SourceAuthorId = null);

/// <summary>What an ordering of a series is, set only from an explicit statement in the source.</summary>
public enum SeriesOrderingKind
{
    /// <summary>Not stated. Must never be read as publication order.</summary>
    Unspecified = 0,
    Publication = 1,
    Reading = 2,
    Chronological = 3
}
