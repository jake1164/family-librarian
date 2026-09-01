namespace FamilyLibrarian.Application.Catalog;

public interface IBookMetadataProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<BookCandidateSearchPage> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken);

    Task<BookCandidate?> GetDetailsAsync(
        string externalId,
        CancellationToken cancellationToken);
}

public sealed record BookCandidateSearchPage(
    IReadOnlyList<BookCandidate> Candidates,
    bool HasMore);
