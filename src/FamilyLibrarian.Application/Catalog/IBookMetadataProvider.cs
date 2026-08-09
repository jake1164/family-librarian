namespace FamilyLibrarian.Application.Catalog;

public interface IBookMetadataProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<IReadOnlyList<BookCandidate>> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken);

    Task<BookCandidate?> GetDetailsAsync(
        string externalId,
        CancellationToken cancellationToken);
}
