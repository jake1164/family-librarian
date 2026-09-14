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
    IReadOnlyList<BookCandidateSource>? MergedSources = null)
{
    public IReadOnlyList<string> Subjects { get; init; } = Subjects ?? [];

    // Populated only by BookCandidateGrouper when this candidate is chosen to
    // represent a group of matching candidates from different providers --
    // every provider's own copy of this record has an empty list here.
    public IReadOnlyList<BookCandidateSource> MergedSources { get; init; } = MergedSources ?? [];
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
    DateOnly? PublicationDate);

public sealed record BookSeriesCandidate(
    string Name,
    string? PositionLabel,
    bool IsPrimary,
    decimal? PositionSort = null,
    bool IsCompleted = false);
