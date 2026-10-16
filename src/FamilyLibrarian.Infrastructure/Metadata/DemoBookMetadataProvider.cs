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
            []),
        // The four entries below exist so the family-librarian-lab
        // integration suite's sample external-provider fixture
        // (samples/FamilyLibrarian.SampleProvider) can resolve a real Work
        // whose title overlaps its own hardcoded catalog -- that provider
        // title-matches by substring, the same way GutenbergProvider does
        // against a synced catalog, so each of these exists purely to give
        // one lab case a real Work to search against. Not otherwise related
        // to each other. Each carries an ISBN deliberately: confirmed
        // against real code (ExternalProviderMatchVerifier.VerifyAsync +
        // DeterministicBookMatcher.ResolveUnique) that BookMatchBasis.Identifier
        // depends only on this Work's own ISBN existing and the provider's
        // search returning exactly one title/author-corroborated candidate
        // -- never on whether the provider's own response carries an
        // identifier at all (the sample provider's candidates carry none).
        new(
            "demo",
            "Family Librarian sample catalog",
            "pride-and-prejudice",
            "Pride and Prejudice",
            ["Jane Austen"],
            "Elizabeth Bennet navigates courtship and class in Regency England.",
            null,
            new DateOnly(1813, 1, 28),
            [new BookEditionCandidate("Pride and Prejudice", "9780141439518", "Ebook", new DateOnly(2003, 1, 1))],
            []),
        new(
            "demo",
            "Family Librarian sample catalog",
            "the-time-machine",
            "The Time Machine",
            ["H. G. Wells"],
            "A Victorian scientist travels far into humanity's future.",
            null,
            new DateOnly(1895, 1, 1),
            [new BookEditionCandidate("The Time Machine", "9780451528551", "Ebook", new DateOnly(2002, 8, 6))],
            []),
        new(
            "demo",
            "Family Librarian sample catalog",
            "debt-of-honor",
            "Debt of Honor",
            ["Tom Clancy"],
            "Jack Ryan confronts an escalating conflict with Japan.",
            null,
            new DateOnly(1994, 1, 1),
            [new BookEditionCandidate("Debt of Honor", "9780425147498", "Ebook", new DateOnly(1995, 7, 1))],
            [new BookSeriesCandidate("Jack Ryan", "6", true)]),
        new(
            "demo",
            "Family Librarian sample catalog",
            "jack-ryan-omnibus",
            "Jack Ryan Omnibus",
            ["Tom Clancy"],
            "A bind-up collecting several Jack Ryan novels in one volume.",
            null,
            null,
            [new BookEditionCandidate("Jack Ryan Omnibus", "9780425147528", "Ebook", null)],
            []),
        // Invented works for the lab's Phase 5 requester-question cases. Each pairs with a fixture candidate in
        // samples/FamilyLibrarian.SampleProvider (QuestionFixtures); the ISBNs are made-up but check-digit valid.
        new(
            "demo",
            "Family Librarian sample catalog",
            "ember-road",
            "Ember Road",
            ["Mara Quillon"],
            "Fixture: book 1 of an invented series.",
            null,
            new DateOnly(2001, 3, 1),
            [new BookEditionCandidate("Ember Road", "9791000000015", "Ebook", new DateOnly(2001, 3, 1))],
            [new BookSeriesCandidate("The Lantern Cycle", "1", true)]),
        new(
            "demo",
            "Family Librarian sample catalog",
            "cinder-gate",
            "Cinder Gate",
            ["Mara Quillon"],
            "Fixture: book 2 of an invented series.",
            null,
            new DateOnly(2003, 3, 1),
            [new BookEditionCandidate("Cinder Gate", "9791000000022", "Ebook", new DateOnly(2003, 3, 1))],
            [new BookSeriesCandidate("The Lantern Cycle", "2", true)]),
        new(
            "demo",
            "Family Librarian sample catalog",
            "ashen-crown",
            "Ashen Crown",
            ["Mara Quillon"],
            "Fixture: book 3 of an invented series.",
            null,
            new DateOnly(2005, 3, 1),
            [new BookEditionCandidate("Ashen Crown", "9791000000039", "Ebook", new DateOnly(2005, 3, 1))],
            [new BookSeriesCandidate("The Lantern Cycle", "3", true)]),
        new(
            "demo",
            "Family Librarian sample catalog",
            "windfall-hollow",
            "Windfall Hollow",
            ["Tessa Brandt"],
            "Fixture: a standalone invented audiobook.",
            null,
            new DateOnly(2010, 6, 1),
            [
                new BookEditionCandidate("Windfall Hollow", "9791000000046", "Ebook", new DateOnly(2010, 6, 1)),
                new BookEditionCandidate("Windfall Hollow", "9791000000053", "Audiobook", new DateOnly(2012, 6, 1))
            ],
            []),
        new(
            "demo",
            "Family Librarian sample catalog",
            "hollowmere",
            "Hollowmere",
            ["Odile Marchetti"],
            "Fixture: a standalone invented book.",
            null,
            new DateOnly(2015, 9, 1),
            [new BookEditionCandidate("Hollowmere", "9791000000060", "Ebook", new DateOnly(2015, 9, 1))],
            []),
        new(
            "demo",
            "Family Librarian sample catalog",
            "saltmarsh-light",
            "Saltmarsh Light",
            ["Ivo Sandoval"],
            "Fixture: a standalone invented audiobook.",
            null,
            new DateOnly(2018, 4, 1),
            [
                new BookEditionCandidate("Saltmarsh Light", "9791000000077", "Ebook", new DateOnly(2018, 4, 1)),
                new BookEditionCandidate("Saltmarsh Light", "9791000000084", "Audiobook", new DateOnly(2019, 4, 1))
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
