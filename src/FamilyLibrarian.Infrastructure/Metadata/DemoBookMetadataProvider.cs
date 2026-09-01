using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// A deterministic development provider used to exercise the catalog UI without
/// sending family search terms to a third party. It will be replaced or joined by
/// configured external providers after the metadata-series spike.
/// </summary>
public sealed class DemoBookMetadataProvider : IBookMetadataProvider
{
    private const int PageSize = 10;

    private static readonly IReadOnlyList<BookCandidate> Books =
    [
        new(
            "demo",
            "Family Librarian sample catalog",
            "the-hobbit",
            "The Hobbit",
            ["J. R. R. Tolkien"],
            "Bilbo Baggins leaves the Shire on an unexpected adventure.",
            null,
            new DateOnly(1937, 9, 21),
            [
                new BookEditionCandidate("The Hobbit", "9780547928227", "Ebook", new DateOnly(2012, 9, 18)),
                new BookEditionCandidate("The Hobbit", "9780008627838", "Audiobook", new DateOnly(2023, 9, 21))
            ],
            [new BookSeriesCandidate("Middle-earth", "0", true)]),
        new(
            "demo",
            "Family Librarian sample catalog",
            "a-wrinkle-in-time",
            "A Wrinkle in Time",
            ["Madeleine L'Engle"],
            "Meg Murry crosses time and space to find her father.",
            null,
            new DateOnly(1962, 1, 1),
            [new BookEditionCandidate("A Wrinkle in Time", "9781250153272", "Ebook", new DateOnly(2017, 1, 3))],
            [new BookSeriesCandidate("Time Quintet", "1", true)]),
        new(
            "demo",
            "Family Librarian sample catalog",
            "project-hail-mary",
            "Project Hail Mary",
            ["Andy Weir"],
            "A lone astronaut must solve an extinction-level problem.",
            null,
            new DateOnly(2021, 5, 4),
            [
                new BookEditionCandidate("Project Hail Mary", "9780593135204", "Ebook", new DateOnly(2021, 5, 4)),
                new BookEditionCandidate("Project Hail Mary", "9780593395561", "Audiobook", new DateOnly(2021, 5, 4))
            ],
            [])
    ];

    public string Id => "demo";

    public string DisplayName => "Family Librarian sample catalog";

    public Task<BookCandidateSearchPage> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken)
    {
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = Books
            .Where(book => terms.All(term => Matches(book, term)))
            .ToArray();
        var offset = (query.Page - 1) * PageSize;
        var results = matches
            .Skip(offset)
            .Take(PageSize)
            .ToArray();

        return Task.FromResult(new BookCandidateSearchPage(
            results,
            matches.Length > offset + results.Length));
    }

    public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Books.SingleOrDefault(book =>
            string.Equals(book.ExternalId, externalId, StringComparison.Ordinal)));
    }

    private static bool Matches(BookCandidate book, string term) =>
        book.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || book.Authors.Any(author => author.Contains(term, StringComparison.OrdinalIgnoreCase))
        || book.Editions.Any(edition => edition.Isbn13?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
}
