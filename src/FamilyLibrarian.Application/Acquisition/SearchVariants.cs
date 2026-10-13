using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The one dimension a search variant changes from the base request. Persisted by name in a later PR, so
/// members are only ever appended.
/// </summary>
public enum SearchVariantKind
{
    Base = 1,
    Isbn = 2,
    AlternateTitle = 3,
    SeriesPosition = 4,
    AuthorVariant = 5,
    AiSuggestion = 6
}

/// <summary>
/// One search shape. <see cref="Key"/> is the normalized value of the single changed dimension (empty for
/// <see cref="SearchVariantKind.Base"/>). Results are always verified against the requested work, never against
/// the variant that found them.
/// </summary>
public sealed record SearchVariant(
    SearchVariantKind Kind, string Key, ExternalProviderWorkEvidence Work, ExternalProviderEditionEvidence Edition)
{
    public SearchVariantRef Ref => new(Kind, Key);
}

/// <summary>
/// Builds the ordered, finite list of searches worth trying for one requested work (precise first, noisiest
/// last). Structured fields only: nothing here sends free text, and an author-only search is deliberately not
/// planned because protocol v2 requires <c>work.title</c>.
/// </summary>
public static class SearchVariantPlanner
{
    public const int MaxKeyLength = 256;

    /// <summary>Exactly the request a recheck has always sent.</summary>
    public static SearchVariant BaseVariant(BookIdentity requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var isbn13 = requested.Isbn13Candidates.FirstOrDefault();
        var work = new ExternalProviderWorkEvidence(
            // Search on the title without its subtitle so a long catalog title still finds the release; the
            // verification afterwards still uses the full identity.
            ExternalSearchTitle.Build(requested.Title),
            Subtitle: null,
            Authors: requested.Authors ?? (requested.Author is null
                ? []
                : [new BookAuthor(requested.Author, "author")]),
            Series: requested.Series ?? [],
            Identifiers: []);
        var edition = new ExternalProviderEditionEvidence(
            requested.Language,
            requested.PublicationYear,
            requested.Publisher,
            Identifiers: isbn13 is null ? [] : [new BookIdentifier("isbn13", isbn13)]);
        return new SearchVariant(SearchVariantKind.Base, string.Empty, work, edition);
    }

    public static IReadOnlyList<SearchVariant> Plan(BookIdentity requested, RequestMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(requested);
        _ = mediaType; // Reserved: every current variant applies to both media types.

        var baseVariant = BaseVariant(requested);
        var variants = new List<SearchVariant> { baseVariant };
        var seen = new HashSet<(SearchVariantKind, string)> { (SearchVariantKind.Base, string.Empty) };

        void Add(SearchVariantKind kind, string key, ExternalProviderWorkEvidence work, ExternalProviderEditionEvidence edition)
        {
            var bounded = Bound(key);
            if (bounded.Length > 0 && seen.Add((kind, bounded)))
            {
                variants.Add(new SearchVariant(kind, bounded, work, edition));
            }
        }

        // Other editions' ISBNs: the base request sends only the first.
        var baseIsbn = requested.Isbn13Candidates.FirstOrDefault();
        foreach (var isbn in requested.Isbn13Candidates
                     .Where(isbn => !string.IsNullOrWhiteSpace(isbn) && !string.Equals(isbn, baseIsbn, StringComparison.Ordinal))
                     .Select(isbn => isbn.Trim())
                     .Order(StringComparer.Ordinal))
        {
            Add(SearchVariantKind.Isbn, isbn, baseVariant.Work,
                baseVariant.Edition with { Identifiers = [new BookIdentifier("isbn13", isbn)] });
        }

        var baseTitle = baseVariant.Work.Title;
        foreach (var alternate in requested.AlternateTitles ?? [])
        {
            if (string.IsNullOrWhiteSpace(alternate))
            {
                continue;
            }

            var reduced = ExternalSearchTitle.Build(alternate);
            if (reduced.Length == 0 || string.Equals(Normalize(reduced), Normalize(baseTitle), StringComparison.Ordinal))
            {
                continue;
            }

            Add(SearchVariantKind.AlternateTitle, Normalize(reduced),
                baseVariant.Work with { Title = reduced }, baseVariant.Edition);
        }

        foreach (var series in requested.Series ?? [])
        {
            if (string.IsNullOrWhiteSpace(series.Name) || string.IsNullOrWhiteSpace(series.Position))
            {
                continue;
            }

            Add(SearchVariantKind.SeriesPosition, $"{Normalize(series.Name)}#{series.Position.Trim()}",
                baseVariant.Work with { Title = $"{series.Name.Trim()} {series.Position.Trim()}" }, baseVariant.Edition);
        }

        var primary = baseVariant.Work.Authors is { Count: > 0 } authors ? authors[0].Name : null;
        foreach (var form in AuthorForms(primary))
        {
            Add(SearchVariantKind.AuthorVariant, Normalize(form),
                baseVariant.Work with { Authors = [new BookAuthor(form, "author")] }, baseVariant.Edition);
        }

        return variants;
    }

    /// <summary>"Last, First" and, when the first name is more than an initial, "F. Last".</summary>
    private static IEnumerable<string> AuthorForms(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            yield break;
        }

        var canonical = AuthorNameForm.Canonical(author);
        var parts = canonical.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            yield break;
        }

        var last = parts[^1];
        var given = string.Join(' ', parts[..^1]);
        var lastFirst = $"{last}, {given}";
        if (!string.Equals(Normalize(lastFirst), Normalize(author), StringComparison.Ordinal))
        {
            yield return lastFirst;
        }

        var firstName = parts[0].TrimEnd('.');
        if (firstName.Length >= 2 && firstName.All(char.IsLetter))
        {
            var initials = $"{char.ToUpperInvariant(firstName[0])}. {last}";
            if (!string.Equals(Normalize(initials), Normalize(author), StringComparison.Ordinal))
            {
                yield return initials;
            }
        }
    }

    /// <summary>Upper-case, single spaces; the same normalisation the browse coalescer uses for its keys.</summary>
    internal static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Bound(string key) => key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
}

/// <summary>
/// Names the search variant that found a candidate (kind + normalized key) so acquiring it can re-run exactly that search.
/// </summary>
public sealed record SearchVariantRef(SearchVariantKind Kind, string Key)
{
    public bool IsBase => Kind == SearchVariantKind.Base;
}
