using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using Microsoft.Extensions.Logging;

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
public sealed partial class CwaCatalogClient(
    IHttpClientFactory httpClientFactory,
    ICwaSettingsStore settingsStore,
    ICredentialProtector protector,
    IBookMatchService matchService,
    ILogger<CwaCatalogClient> logger) : ICwaCatalogClient
{
    private static readonly Regex BookIdPattern = new(@"/opds/(?:book|download)/(\d+)", RegexOptions.Compiled);
    private static readonly Regex TitleSearchTokenPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public async Task<BookMatchResult> FindImportedFileAsync(
        string title, string format, long sizeBytes, string sha256, CancellationToken cancellationToken,
        string? acceptedLanguage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedFormat = format.TrimStart('.').ToLowerInvariant();
        if (sizeBytes <= 0 || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit) ||
            normalizedFormat.Length == 0 || !normalizedFormat.All(char.IsAsciiLetterOrDigit))
        {
            return BookMatchResult.NoMatchResult;
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return BookMatchResult.NoMatchResult;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            // The title discovers candidates only. Author differences can be the very reason this approved file
            // needed a release; neither a title nor a model verdict proves which destination entry holds its bytes.
            var candidates = new Dictionary<string, CandidateBook>(StringComparer.Ordinal);
            foreach (var query in TitleSearchQueries(title).Append(null))
            {
                var body = query is null
                    ? await SendRecentBooksAsync(settings, deadline.Token)
                    : await SendSearchAsync(query, settings, deadline.Token);
                var document = body is null ? null : TryParseAtom(body);
                if (document is null) continue;

                foreach (var candidate in ExtractCandidates(body!))
                {
                    var match = await matchService.MatchByTitleAuthorAsync(
                        title, null, [candidate], deadline.Token, acceptedLanguage);
                    if (match.Decision != BookMatchDecision.Match) continue;

                    var downloadUri = ImportedFileUri(settings, candidate.ExternalId, normalizedFormat);
                    if (!document.Descendants(AtomNamespace + "link").Any(link =>
                            link.Attribute("rel")?.Value == "http://opds-spec.org/acquisition" &&
                            Uri.TryCreate(new Uri(settings.OpdsBaseUrl.TrimEnd('/') + "/"), link.Attribute("href")?.Value, out var offered) &&
                            offered == downloadUri)) continue;

                    candidates.TryAdd(candidate.ExternalId, candidate);
                    // Do not truncate and accidentally call one copy unique when an unchecked entry may match too.
                    if (candidates.Count > 8) return BookMatchResult.NoMatchResult;
                }
            }

            var confirmed = new List<CandidateBook>();
            foreach (var candidate in candidates.Values)
            {
                if (await HasApprovedBytesAsync(settings, candidate.ExternalId, normalizedFormat, sizeBytes, sha256, deadline.Token))
                {
                    confirmed.Add(candidate);
                }
            }

            var result = confirmed.Count switch
            {
                0 => BookMatchResult.NoMatchResult,
                1 => BookMatchResult.Match(confirmed[0]),
                _ => BookMatchResult.Ambiguous(confirmed)
            };
            LogImportedFileDecision(title, result.Decision, result.MatchedId);
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or IOException or OperationCanceledException)
        {
            LogImportedFileUnavailable(title, exception.GetType().Name);
            return BookMatchResult.NoMatchResult;
        }
    }

    private static Uri ImportedFileUri(Domain.Publishing.CwaSettings settings, string bookId, string format) =>
        new($"{settings.OpdsBaseUrl!.TrimEnd('/')}/opds/download/{bookId}/{format}/");

    private async Task<bool> HasApprovedBytesAsync(
        Domain.Publishing.CwaSettings settings, string bookId, string format, long expectedSize, string expectedHash,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ImportedFileUri(settings, bookId, format));
        ApplyRequestHeaders(request, settings.OpdsUsername, ResolveOpdsPassword(settings));
        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // An unreadable candidate cannot be ruled out of a uniqueness decision. Retry the entire lookup later.
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } size && size != expectedSize) return false;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            // Check actual EOF, including when Content-Length is missing or false. Read at most one byte beyond the
            // approved size, without buffering a book in memory or trusting a potentially unbounded response body.
            var limit = (int)Math.Min(buffer.Length, expectedSize - total < buffer.Length ? expectedSize - total + 1 : buffer.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, limit), cancellationToken);
            if (read == 0) break;
            if (read > expectedSize - total) return false;
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return total == expectedSize && string.Equals(
            Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

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
        CancellationToken cancellationToken,
        string? acceptedLanguage = null)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            LogOpdsNotConfigured(title, author);
            return BookMatchResult.NoMatchResult;
        }

        foreach (var isbn in isbn13Candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        {
            var isbnBody = await SendSearchAsync(isbn, settings, cancellationToken);
            if (isbnBody is null)
            {
                continue;
            }

            var isbnCandidates = ExtractCandidates(isbnBody);
            var isbnResult = await matchService.ResolveUniqueAsync(
                title, author, isbnCandidates, cancellationToken, acceptedLanguage);
            if (isbnResult.Decision == BookMatchDecision.Match)
            {
                LogIsbnMatch(title, author, isbnResult.MatchedId, isbn);
                return isbnResult;
            }
        }

        foreach (var titleQuery in TitleSearchQueries(title))
        {
            var titleBody = await SendSearchAsync(titleQuery, settings, cancellationToken);
            if (titleBody is null)
            {
                // A failed request for this one spelling (e.g. a transient
                // 5xx or a query CWA's search happens to choke on) is not
                // evidence that the remaining fallback queries would fail
                // too -- keep trying them rather than treating a single bad
                // response as proof the book is missing.
                continue;
            }

            var titleCandidates = ExtractCandidates(titleBody);
            var titleResult = await matchService.MatchByTitleAuthorAsync(
                title, author, titleCandidates, cancellationToken, acceptedLanguage);
            LogTitleQueryResolved(titleQuery, title, author, titleResult.Decision);
            if (titleResult.Decision != BookMatchDecision.NoMatch)
            {
                return titleResult;
            }
        }

        // CWA's title index may use punctuation that the canonical request
        // lacks. Its newest-books feed is ordered by Calibre timestamp, so it
        // is a bounded, read-only recovery path for a handoff that CWA has
        // already ingested but its title search did not return.
        var recentBody = await SendRecentBooksAsync(settings, cancellationToken);
        if (recentBody is null)
        {
            LogRecentBooksFeedUnavailable(title, author);
            return BookMatchResult.NoMatchResult;
        }

        var recentCandidates = ExtractCandidates(recentBody);
        var recentResult = await matchService.MatchByTitleAuthorAsync(
            title, author, recentCandidates, cancellationToken, acceptedLanguage);
        LogFinalDecision(title, author, recentResult.Decision);
        return recentResult;
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
        Message = "cwa.catalog.lookup.opds_not_configured: title='{Title}' author='{Author}'")]
    private partial void LogOpdsNotConfigured(string title, string? author);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
        Message = "cwa.catalog.lookup.isbn_match: title='{Title}' author='{Author}' bookId={BookId} isbn='{Isbn}'")]
    private partial void LogIsbnMatch(string title, string? author, string? bookId, string isbn);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information,
        Message = "cwa.catalog.lookup.title_query_resolved: query='{Query}' title='{Title}' author='{Author}' decision={Decision}")]
    private partial void LogTitleQueryResolved(string query, string title, string? author, BookMatchDecision decision);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information,
        Message = "cwa.catalog.lookup.recent_books_feed_unavailable: title='{Title}' author='{Author}'")]
    private partial void LogRecentBooksFeedUnavailable(string title, string? author);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Information,
        Message = "cwa.catalog.lookup.final_decision: title='{Title}' author='{Author}' decision={Decision}")]
    private partial void LogFinalDecision(string title, string? author, BookMatchDecision decision);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Information,
        Message = "cwa.catalog.lookup.imported_file: title='{Title}' decision={Decision} bookId={BookId}")]
    private partial void LogImportedFileDecision(string title, BookMatchDecision decision, string? bookId);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Warning,
        Message = "cwa.catalog.lookup.imported_file_unavailable: title='{Title}' failure={Failure}")]
    private partial void LogImportedFileUnavailable(string title, string failure);

    /// <summary>
    /// CWA currently applies a literal substring search to its title index.
    /// A request such as <c>Moby Dick</c> therefore cannot find CWA's
    /// <c>Moby-Dick; or, The Whale</c> entry until a punctuation-independent
    /// token query is tried. These queries only discover candidates; the
    /// shared matcher still decides whether precisely one is the requested
    /// work. Four fallback tokens bound the added load for unusually long
    /// titles while retaining the original exact query first. If all title
    /// searches miss, <c>/opds/new</c> is checked once; a disabled Recent Books
    /// feed simply produces no match.
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
        var requestUri = $"{settings.OpdsBaseUrl!.TrimEnd('/')}/opds/search/{Uri.EscapeDataString(query)}";
        return await SendGetAsync(requestUri, settings, cancellationToken);
    }

    private async Task<string?> SendRecentBooksAsync(
        Domain.Publishing.CwaSettings settings, CancellationToken cancellationToken)
    {
        var requestUri = $"{settings.OpdsBaseUrl!.TrimEnd('/')}/opds/new";
        return await SendGetAsync(requestUri, settings, cancellationToken);
    }

    private async Task<string?> SendGetAsync(
        string requestUri, Domain.Publishing.CwaSettings settings, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyRequestHeaders(request, settings.OpdsUsername, ResolveOpdsPassword(settings));

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

    private static void ApplyRequestHeaders(HttpRequestMessage request, string? username, string? password)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FamilyLibrarian", "0.1"));
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
    /// Calibre-Web's OPDS entries carry a Dublin Core Terms
    /// <c>&lt;dcterms:language&gt;</c> element (e.g. <c>eng</c>) per book —
    /// used only to populate <see cref="CandidateBook.Language"/> for
    /// ACCURACY-1's language filtering. If a given feed never emits it, entries
    /// simply come back with <c>Language = null</c> (unspecified, still
    /// eligible), not an error.
    /// </summary>
    private static readonly XNamespace DcTermsNamespace = "http://purl.org/dc/terms/";

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
            var entryLanguage = entry.Element(DcTermsNamespace + "language")?.Value;
            candidates.Add(new CandidateBook(id, entryTitle, entryAuthor, entryLanguage));
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
