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
    string? SourceUrl = null)
{
    public IReadOnlyList<string> Subjects { get; init; } = Subjects ?? [];
}

public sealed record BookEditionCandidate(
    string Title,
    string? Isbn13,
    string Format,
    DateOnly? PublicationDate);

public sealed record BookSeriesCandidate(
    string Name,
    string? PositionLabel,
    bool IsPrimary);
