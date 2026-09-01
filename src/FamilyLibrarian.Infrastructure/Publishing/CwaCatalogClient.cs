using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyLibrarian.Application.Integrations;
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
    ICredentialProtector protector) : ICwaCatalogClient
{
    private static readonly Regex BookIdPattern = new(@"/opds/(?:book|download)/(\d+)", RegexOptions.Compiled);

    /// <remarks>
    /// Identifier-first: an ISBN candidate is tried as a search query before
    /// title/author matching, since a numeric ISBN string is effectively
    /// unique and does not suffer the title-collision ambiguity a substring
    /// match does. Whether CWA's search actually indexes ISBN is unconfirmed
    /// (see docs/03 "OPDS integration stability") — if it doesn't, every ISBN
    /// query simply returns zero or many results and this falls through to
    /// the title/author fallback below, so there is no harm in trying.
    /// </remarks>
    public async Task<string?> FindBookIdAsync(
        string title,
        string? author,
        IReadOnlyCollection<string> isbn13Candidates,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return null;
        }

        foreach (var isbn in isbn13Candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        {
            var isbnBody = await SendSearchAsync(isbn, settings, cancellationToken);
            if (isbnBody is null)
            {
                continue;
            }

            var isbnMatches = ExtractBookIds(isbnBody);
            if (isbnMatches.Count == 1)
            {
                return isbnMatches.Single();
            }
        }

        var titleBody = await SendSearchAsync(title, settings, cancellationToken);
        if (titleBody is null)
        {
            return null;
        }

        var titleMatches = ExtractMatchingBookIds(titleBody, title, author);
        return titleMatches.Count == 1 ? titleMatches.Single() : null;
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
    /// Every distinct Calibre book id found across every entry in the feed,
    /// unfiltered — used for an ISBN search, where the query string itself is
    /// already the filter. A parse failure yields no ids rather than an
    /// exception, matching this client's "not found is normal" posture.
    /// </summary>
    private static HashSet<string> ExtractBookIds(string atomXml)
    {
        var document = TryParseAtom(atomXml);
        if (document is null)
        {
            return [];
        }

        var ids = new HashSet<string>();
        foreach (var entry in document.Descendants(AtomNamespace + "entry"))
        {
            var id = FirstAcquisitionLinkBookId(entry);
            if (id is not null)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Every distinct Calibre book id among entries whose title (and author,
    /// when known) matches. Deliberately collects every match rather than
    /// returning the first — see <see cref="FindBookIdAsync"/> — so the
    /// caller can tell a single confident match from an ambiguous one.
    /// </summary>
    private static HashSet<string> ExtractMatchingBookIds(string atomXml, string title, string? author)
    {
        var document = TryParseAtom(atomXml);
        if (document is null)
        {
            return [];
        }

        var ids = new HashSet<string>();
        foreach (var entry in document.Descendants(AtomNamespace + "entry"))
        {
            var entryTitle = entry.Element(AtomNamespace + "title")?.Value;
            if (string.IsNullOrWhiteSpace(entryTitle) ||
                !entryTitle.Contains(title, StringComparison.OrdinalIgnoreCase) ||
                IsUnwantedVariant(entryTitle, title))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                var entryAuthor = entry.Element(AtomNamespace + "author")?.Element(AtomNamespace + "name")?.Value;
                if (!string.IsNullOrWhiteSpace(entryAuthor) &&
                    !entryAuthor.Contains(author, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var id = FirstAcquisitionLinkBookId(entry);
            if (id is not null)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Known, cheap textual markers of a different product than the one
    /// requested, even when the requested title appears in it as a
    /// substring — e.g. "Summary of Debt of Honor" or "Debt of Honor /
    /// Executive Orders". Not exhaustive; see
    /// docs/family-librarian-book-matching-design-findings.md §5/§6/§8 — a
    /// title-substring match is grounds for further comparison, not
    /// automatic identity, and a derivative or combined-work title is
    /// negative evidence even when otherwise unambiguous.
    /// </summary>
    private static readonly string[] DerivativeTitleMarkers =
    [
        "summary of", "study guide", "companion to", "workbook for", "analysis of",
        "cliffsnotes", "cliff notes", "sparknotes", "excerpt", "sample chapter",
        "abridged", "omnibus", "box set", "boxed set",
    ];

    private static bool IsUnwantedVariant(string entryTitle, string requestedTitle)
    {
        if (NormalizeForExactComparison(entryTitle) == NormalizeForExactComparison(requestedTitle))
        {
            // An exact title match is accepted regardless of these markers --
            // e.g. a work whose own real title happens to be "Box Set".
            return false;
        }

        var lowered = entryTitle.ToLowerInvariant();
        return DerivativeTitleMarkers.Any(lowered.Contains) ||
            lowered.Contains('/') ||
            Regex.IsMatch(lowered, @"\s&\s");
    }

    private static string NormalizeForExactComparison(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
