using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// Real HTTP contract, verified against the actual image the lab runs
/// (<c>crocodilestick/Calibre-Web-Automated</c>) since nothing documented it:
/// <c>GET /login</c> to scrape a CSRF token and pick up the anonymous session
/// cookie, <c>POST /login</c> (Flask-Login regenerates the session on success,
/// so the cookie must be re-merged, not assumed unchanged), <c>GET /</c> to
/// scrape a fresh CSRF token from the authenticated session (the pre-login
/// token is signed against the pre-login session and is not assumed valid
/// after login), then <c>POST /send_selected/&lt;book_id&gt;</c>. CWA always
/// answers the send with HTTP 200 and a JSON array
/// <c>[{"type": "success"|"danger", "message": "..."}]</c> -- business
/// failure is never a non-200 status.
/// </summary>
public sealed partial class CwaEreaderSessionClient(
    IHttpClientFactory httpClientFactory,
    ILogger<CwaEreaderSessionClient> logger) : ICwaEreaderSessionClient
{
    public const string HttpClientName = "cwa-ereader-session";

    private static readonly Regex CsrfInputTagPattern = new(
        """<input[^>]*name=["']csrf_token["'][^>]*>""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ValueAttributePattern = new(
        """value=["']([^"']*)["']""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<CwaEreaderSendResult> SendSelectedAsync(
        CwaSettings settings,
        string serviceAccountUsername,
        string serviceAccountPassword,
        string cwaBookId,
        string bookFormat,
        bool convert,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.OpdsBaseUrl!.TrimEnd('/');
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            var (loginSucceeded, cookies, csrfToken) = await SignInAsync(
                client, baseUrl, serviceAccountUsername, serviceAccountPassword, cancellationToken);
            if (!loginSucceeded || csrfToken is null)
            {
                LogLoginFailed(baseUrl);
                return new CwaEreaderSendResult(CwaEreaderSendStatus.LoginFailed, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/send_selected/{cwaBookId}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["selected_emails"] = recipientEmail,
                    ["book_format"] = bookFormat,
                    ["convert"] = convert ? "1" : "0",
                    ["csrf_token"] = csrfToken,
                })
            };
            ApplyCookies(request, cookies);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogUnexpectedSendStatus((int)response.StatusCode);
                return new CwaEreaderSendResult(CwaEreaderSendStatus.TransportFailure, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseSendResponse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTransportFailure(exception.GetType().Name);
            return new CwaEreaderSendResult(CwaEreaderSendStatus.TransportFailure, null);
        }
    }

    public async Task<CwaEreaderLoginResult> TestLoginAsync(
        CwaSettings settings,
        string serviceAccountUsername,
        string serviceAccountPassword,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.OpdsBaseUrl!.TrimEnd('/');
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            var (loginSucceeded, _, csrfToken) = await SignInAsync(
                client, baseUrl, serviceAccountUsername, serviceAccountPassword, cancellationToken);
            return loginSucceeded && csrfToken is not null
                ? new CwaEreaderLoginResult(true, "Signed in to CWA as the e-reader delivery service account.")
                : new CwaEreaderLoginResult(false, "The e-reader delivery service account could not sign in to CWA.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTransportFailure(exception.GetType().Name);
            return new CwaEreaderLoginResult(false, "The e-reader delivery destination could not be reached.");
        }
    }

    /// <summary>
    /// Steps 1-3 of the flow: fetch the login page, sign in, then fetch an
    /// authenticated page for a fresh, post-login CSRF token. Returns
    /// <c>(false, ..., null)</c> for any failure along the way -- never throws
    /// for an ordinary login rejection.
    /// </summary>
    private static async Task<(bool Succeeded, Dictionary<string, string> Cookies, string? CsrfToken)> SignInAsync(
        HttpClient client, string baseUrl, string username, string password, CancellationToken cancellationToken)
    {
        using var loginPageResponse = await client.GetAsync($"{baseUrl}/login", cancellationToken);
        var loginPageBody = await loginPageResponse.Content.ReadAsStringAsync(cancellationToken);
        var preLoginToken = ScrapeCsrfToken(loginPageBody);
        var cookies = MergeSetCookies(loginPageResponse, new Dictionary<string, string>(StringComparer.Ordinal));
        if (preLoginToken is null)
        {
            return (false, cookies, null);
        }

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
                ["csrf_token"] = preLoginToken,
            })
        };
        ApplyCookies(loginRequest, cookies);

        using var loginResponse = await client.SendAsync(loginRequest, cancellationToken);
        var loginSucceeded = loginResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther &&
            !(loginResponse.Headers.Location?.OriginalString.Contains("/login", StringComparison.OrdinalIgnoreCase) ?? true);
        if (!loginSucceeded)
        {
            return (false, cookies, null);
        }

        cookies = MergeSetCookies(loginResponse, cookies);

        using var indexRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/");
        ApplyCookies(indexRequest, cookies);
        using var indexResponse = await client.SendAsync(indexRequest, cancellationToken);
        cookies = MergeSetCookies(indexResponse, cookies);
        var indexBody = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
        var postLoginToken = ScrapeCsrfToken(indexBody);

        return (postLoginToken is not null, cookies, postLoginToken);
    }

    private static CwaEreaderSendResult ParseSendResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return new CwaEreaderSendResult(CwaEreaderSendStatus.TransportFailure, null);
            }

            var first = document.RootElement[0];
            var type = first.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
            var message = first.TryGetProperty("message", out var messageProperty) ? messageProperty.GetString() : null;

            return type switch
            {
                "success" => new CwaEreaderSendResult(CwaEreaderSendStatus.Success, message),
                "danger" => new CwaEreaderSendResult(CwaEreaderSendStatus.SendRejected, message),
                _ => new CwaEreaderSendResult(CwaEreaderSendStatus.TransportFailure, null),
            };
        }
        catch (JsonException)
        {
            return new CwaEreaderSendResult(CwaEreaderSendStatus.TransportFailure, null);
        }
    }

    private static string? ScrapeCsrfToken(string html)
    {
        var tag = CsrfInputTagPattern.Match(html);
        if (!tag.Success)
        {
            return null;
        }

        var value = ValueAttributePattern.Match(tag.Value);
        return value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : null;
    }

    private static Dictionary<string, string> MergeSetCookies(
        HttpResponseMessage response, Dictionary<string, string> existing)
    {
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return merged;
        }

        foreach (var raw in setCookies)
        {
            var pair = raw.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator > 0)
            {
                merged[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }

        return merged;
    }

    private static void ApplyCookies(HttpRequestMessage request, Dictionary<string, string> cookies)
    {
        if (cookies.Count == 0)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            "Cookie", string.Join("; ", cookies.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "cwa.ereader.login.failed: base_url='{BaseUrl}'")]
    private partial void LogLoginFailed(string baseUrl);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning,
        Message = "cwa.ereader.send.unexpected_status: status={StatusCode}")]
    private partial void LogUnexpectedSendStatus(int statusCode);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
        Message = "cwa.ereader.transport_failure: exception_type='{ExceptionType}'")]
    private partial void LogTransportFailure(string exceptionType);
}
