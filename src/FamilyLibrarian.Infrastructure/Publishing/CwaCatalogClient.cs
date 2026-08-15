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

    public async Task<string?> FindBookIdAsync(string title, string? author, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return null;
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var requestUri = $"{settings.OpdsBaseUrl.TrimEnd('/')}/opds/search/{Uri.EscapeDataString(title)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyBasicAuth(request, settings.OpdsUsername, ResolveOpdsPassword(settings));

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseFirstMatchingBookId(body, title, author);
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

    /// <summary>
    /// Finds the first Atom entry whose title (and author, when known)
    /// matches, and extracts its Calibre book id from an acquisition link.
    /// "Not found" is a normal, common outcome here — CWA's ingest is
    /// asynchronous — so a parse failure or empty result is just <c>null</c>,
    /// never an exception.
    /// </summary>
    private static string? ParseFirstMatchingBookId(string atomXml, string title, string? author)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(atomXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var entryTitle = entry.Element(atom + "title")?.Value;
            if (string.IsNullOrWhiteSpace(entryTitle) ||
                !entryTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                var entryAuthor = entry.Element(atom + "author")?.Element(atom + "name")?.Value;
                if (!string.IsNullOrWhiteSpace(entryAuthor) &&
                    !entryAuthor.Contains(author, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            foreach (var link in entry.Elements(atom + "link"))
            {
                var href = link.Attribute("href")?.Value;
                var match = href is null ? null : BookIdPattern.Match(href);
                if (match is { Success: true })
                {
                    return match.Groups[1].Value;
                }
            }
        }

        return null;
    }
}
