using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// A thin OPDS (Atom) client against Calibre-Web's search feed, used only to
/// confirm a handed-off book landed in the catalog.
/// </summary>
/// <remarks>
/// Reads <c>CwaSettings</c> fresh on every call rather than baking a base
/// address into a typed <c>HttpClient</c> at DI-registration time — unlike a
/// metadata provider's fixed API host, the OPDS base URL is itself
/// admin-configurable and can change at any time (see
/// <c>MetadataCredentialSource</c> for the same "resolve fresh, don't cache"
/// rationale applied to a provider credential).
/// </remarks>
public sealed class CwaCatalogClient(
    IHttpClientFactory httpClientFactory,
    ICwaSettingsStore settingsStore,
    ICredentialProtector protector,
    IBookMatchService matchService) : ICwaCatalogClient
{
    private static readonly Regex BookIdPattern = new(@"/opds/(?:book|download)/(\d+)", RegexOptions.Compiled);
    private static readonly Regex TitleSearchTokenPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <remarks>
    /// Identifier-first: an ISBN candidate is tried as a search query before
    /// title/author matching, since a numeric ISBN string is effectively
    /// unique and does not suffer the title-collision ambiguity a substring
    /// match does. Whether CWA's search actually indexes ISBN is unconfirmed
    /// (see docs/03 "OPDS integration stability") — if it doesn't, every ISBN
    /// query simply returns zero or many results and this falls through to
    /// the title/author fallback below, so there is no harm in trying.
    /// </remarks>
    public async Task<BookMatchResult> FindBookIdAsync(
        string title,
        string? author,
        IReadOnlyCollection<string> isbn13Candidates,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return BookMatchResult.NoMatchResult;
        }

        foreach (var isbn in isbn13Candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        {
            var isbnBody = await SendSearchAsync(isbn, settings, cancellationToken);
            if (isbnBody is null)
            {
                continue;
            }

            var isbnResult = await matchService.ResolveUniqueAsync(
                title, author, ExtractCandidates(isbnBody), cancellationToken);
            if (isbnResult.Decision == BookMatchDecision.Match)
            {
                return isbnResult;
            }
        }

        foreach (var titleQuery in TitleSearchQueries(title))
        {
            var titleBody = await SendSearchAsync(titleQuery, settings, cancellationToken);
            if (titleBody is null)
            {
                // A failed OPDS request is not evidence that another spelling
                // will work. Preserve the existing best-effort no-match
                // behavior instead of amplifying a temporary outage.
                return BookMatchResult.NoMatchResult;
            }

            var titleResult = await matchService.MatchByTitleAuthorAsync(
                title, author, ExtractCandidates(titleBody), cancellationToken);
            if (titleResult.Decision != BookMatchDecision.NoMatch)
            {
                return titleResult;
            }
        }

        return BookMatchResult.NoMatchResult;
    }

    /// <summary>
    /// CWA currently applies a literal substring search to its title index.
    /// A request such as <c>Moby Dick</c> therefore cannot find CWA's
    /// <c>Moby-Dick; or, The Whale</c> entry until a punctuation-independent
    /// token query is tried. These queries only discover candidates; the
    /// shared matcher still decides whether precisely one is the requested
    /// work. Four fallback tokens bound the added load for unusually long
    /// titles while retaining the original exact query first.
    /// </summary>
    private static IEnumerable<string> TitleSearchQueries(string title)
    {
        yield return title;

        var queries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { title };
        var fallbackCount = 0;
        foreach (Match token in TitleSearchTokenPattern.Matches(title))
        {
            var query = token.Value;
            if (query.Length < 3 || !queries.Add(query))
            {
                continue;
            }

            yield return query;
            fallbackCount++;
            if (fallbackCount == 4)
            {
                yield break;
            }
        }
    }

    private async Task<string?> SendSearchAsync(
        string query, Domain.Publishing.CwaSettings settings, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var requestUri = $"{settings.OpdsBaseUrl!.TrimEnd('/')}/opds/search/{Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyBasicAuth(request, settings.OpdsUsername, ResolveOpdsPassword(settings));

        using var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : null;
    }

    private string? ResolveOpdsPassword(Domain.Publishing.CwaSettings settings) =>
        settings.HasOpdsPassword
            ? protector.Unprotect(
                PublishingSecretPurposes.CwaOpdsPassword, settings.ProtectedOpdsPassword!, settings.OpdsPasswordFormatVersion)
            : null;

    private static void ApplyBasicAuth(HttpRequestMessage request, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var raw = $"{username}:{password}";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
    }

    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// Every entry in the feed, normalized to a <see cref="CandidateBook"/>.
    /// Filtering (unwanted variants, title/author matching, uniqueness) is
    /// delegated to <see cref="IBookMatchService"/> rather than done here — a
    /// parse failure yields no candidates rather than an exception, matching
    /// this client's "not found is normal" posture.
    /// </summary>
    private static CandidateBook[] ExtractCandidates(string atomXml)
    {
        var document = TryParseAtom(atomXml);
        if (document is null)
        {
            return [];
        }

        var candidates = new HashSet<CandidateBook>();
        foreach (var entry in document.Descendants(AtomNamespace + "entry"))
        {
            var id = FirstAcquisitionLinkBookId(entry);
            var entryTitle = entry.Element(AtomNamespace + "title")?.Value;
            if (id is null || string.IsNullOrWhiteSpace(entryTitle))
            {
                continue;
            }

            var entryAuthor = entry.Element(AtomNamespace + "author")?.Element(AtomNamespace + "name")?.Value;
            candidates.Add(new CandidateBook(id, entryTitle, entryAuthor));
        }

        return candidates.ToArray();
    }

    private static string? FirstAcquisitionLinkBookId(XElement entry)
    {
        foreach (var link in entry.Elements(AtomNamespace + "link"))
        {
            var href = link.Attribute("href")?.Value;
            var match = href is null ? null : BookIdPattern.Match(href);
            if (match is { Success: true })
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static XDocument? TryParseAtom(string atomXml)
    {
        try
        {
            return XDocument.Parse(atomXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
