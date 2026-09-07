using System.Net;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

/// <summary>
/// The stateful login/CSRF/cookie flow against CWA's real HTTP contract
/// (<c>GET /login</c> -&gt; <c>POST /login</c> -&gt; <c>GET /</c> -&gt;
/// <c>POST /send_selected/&lt;book_id&gt;</c>), verified against the real
/// lab CWA container's actual responses before these fixtures were written.
/// </summary>
[TestClass]
public sealed class CwaEreaderSessionClientTests
{
    private const string BookId = "42";
    private const string RecipientEmail = "reader@kindle.com";

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly CwaSettings ConfiguredSettings = CreateSettings();

    [TestMethod]
    public async Task ASuccessfulSendUsesThePostLoginCookieAndCsrfTokenNotThePreLoginOnes()
    {
        var handler = new ScriptedHandler();
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "correct-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.Success, result.Status);
        var sendRequest = handler.Requests.Single(r => r.Path == $"/send_selected/{BookId}");
        StringAssert.Contains(sendRequest.CookieHeader, "session=post-login-cookie");
        StringAssert.DoesNotMatch(sendRequest.CookieHeader ?? "", new System.Text.RegularExpressions.Regex("pre-login-cookie"));
        Assert.AreEqual("post-login-token", sendRequest.FormFields["csrf_token"]);
        Assert.AreEqual(RecipientEmail, sendRequest.FormFields["selected_emails"]);
        Assert.AreEqual("EPUB", sendRequest.FormFields["book_format"]);
        Assert.AreEqual("0", sendRequest.FormFields["convert"]);
    }

    [TestMethod]
    public async Task ALoginPageReRenderInsteadOfARedirectIsALoginFailureAndNeverSends()
    {
        var handler = new ScriptedHandler { LoginPostStatusCode = HttpStatusCode.OK };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "wrong-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.LoginFailed, result.Status);
        Assert.IsFalse(handler.Requests.Any(r => r.Path.StartsWith("/send_selected", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ARedirectBackToLoginIsALoginFailureAndNeverSends()
    {
        var handler = new ScriptedHandler { LoginPostLocation = "/login" };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "wrong-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.LoginFailed, result.Status);
        Assert.IsFalse(handler.Requests.Any(r => r.Path.StartsWith("/send_selected", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ADangerResponsePassesCwaMessageThrough()
    {
        var handler = new ScriptedHandler
        {
            SendResponseBody = """[{"type": "danger", "message": "Please configure the SMTP mail settings first..."}]"""
        };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "correct-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.SendRejected, result.Status);
        Assert.AreEqual("Please configure the SMTP mail settings first...", result.CwaMessage);
    }

    [TestMethod]
    public async Task AnUnreachableLoginPageIsATransportFailure()
    {
        var handler = new ScriptedHandler { ThrowOnLoginGet = true };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "correct-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.TransportFailure, result.Status);
    }

    [TestMethod]
    public async Task AnUnreachableSendEndpointIsATransportFailure()
    {
        var handler = new ScriptedHandler { ThrowOnSendPost = true };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "correct-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.TransportFailure, result.Status);
    }

    [TestMethod]
    public async Task MissingCsrfMarkupOnTheLoginPageIsALoginFailureNotAnException()
    {
        var handler = new ScriptedHandler { LoginPageCsrfToken = null };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "correct-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.LoginFailed, result.Status);
    }

    [TestMethod]
    public async Task AMalformedSendResponseBodyIsATransportFailureNotAnException()
    {
        var handler = new ScriptedHandler { SendResponseBody = "not json" };
        var client = CreateClient(handler);

        var result = await client.SendSelectedAsync(
            ConfiguredSettings, "service-account", "correct-password",
            BookId, "EPUB", convert: false, RecipientEmail, CancellationToken.None);

        Assert.AreEqual(CwaEreaderSendStatus.TransportFailure, result.Status);
    }

    [TestMethod]
    public async Task TestLoginAsyncSucceedsWithoutEverCallingSendSelected()
    {
        var handler = new ScriptedHandler();
        var client = CreateClient(handler);

        var result = await client.TestLoginAsync(
            ConfiguredSettings, "service-account", "correct-password", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(handler.Requests.Any(r => r.Path.StartsWith("/send_selected", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TestLoginAsyncFailsWhenTheLoginIsRejected()
    {
        var handler = new ScriptedHandler { LoginPostStatusCode = HttpStatusCode.OK };
        var client = CreateClient(handler);

        var result = await client.TestLoginAsync(
            ConfiguredSettings, "service-account", "wrong-password", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    private static CwaEreaderSessionClient CreateClient(HttpMessageHandler handler) =>
        new(new TestHttpClientFactory(handler), NullLogger<CwaEreaderSessionClient>.Instance);

    private static CwaSettings CreateSettings()
    {
        var settings = new CwaSettings(Now);
        settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null,
            CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example.test", null, null, "service-account",
            null, Now);
        return settings;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record RecordedRequest(string Method, string Path, string? CookieHeader, Dictionary<string, string> FormFields);

    /// <summary>
    /// Scripts the four-request CWA login/send flow exactly as observed
    /// against the real lab CWA container: <c>GET /login</c> (CSRF token +
    /// anonymous session cookie), <c>POST /login</c> (302 to "/" with a
    /// regenerated session cookie on success), <c>GET /</c> (a fresh,
    /// post-login CSRF token), <c>POST /send_selected/&lt;id&gt;</c> (always
    /// HTTP 200 with a <c>[{"type": ..., "message": ...}]</c> JSON body).
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public string? LoginPageCsrfToken { get; set; } = "pre-login-token";

        public HttpStatusCode LoginPostStatusCode { get; set; } = HttpStatusCode.Redirect;

        public string LoginPostLocation { get; set; } = "/";

        public string IndexPageCsrfToken { get; set; } = "post-login-token";

        public string SendResponseBody { get; set; } =
            """[{"type": "success", "message": "Success! Book queued for sending."}]""";

        public bool ThrowOnLoginGet { get; set; }

        public bool ThrowOnSendPost { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var cookieHeader = request.Headers.TryGetValues("Cookie", out var cookieValues)
                ? string.Join("; ", cookieValues)
                : null;
            var formFields = request.Content is null
                ? []
                : ParseForm(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(new RecordedRequest(request.Method.Method, path, cookieHeader, formFields));

            if (request.Method == HttpMethod.Get && path == "/login")
            {
                if (ThrowOnLoginGet)
                {
                    throw new HttpRequestException("simulated connection failure");
                }

                var body = LoginPageCsrfToken is null
                    ? "<html><body>no csrf field here</body></html>"
                    : $"""<html><body><form><input type="hidden" name="csrf_token" value="{LoginPageCsrfToken}"></form></body></html>""";
                return WithSetCookie(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) },
                    "session=pre-login-cookie");
            }

            if (request.Method == HttpMethod.Post && path == "/login")
            {
                var response = new HttpResponseMessage(LoginPostStatusCode) { Content = new StringContent(string.Empty) };
                if (LoginPostStatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther)
                {
                    response.Headers.Location = new Uri(LoginPostLocation, UriKind.RelativeOrAbsolute);
                    return WithSetCookie(response, "session=post-login-cookie");
                }

                return response;
            }

            if (request.Method == HttpMethod.Get && path == "/")
            {
                var body = $"""<html><body><form><input type="hidden" name="csrf_token" value="{IndexPageCsrfToken}"></form></body></html>""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }

            if (request.Method == HttpMethod.Post && path == $"/send_selected/{BookId}")
            {
                if (ThrowOnSendPost)
                {
                    throw new HttpRequestException("simulated connection failure");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SendResponseBody, System.Text.Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException($"Unscripted request: {request.Method} {path}");
        }

        private static HttpResponseMessage WithSetCookie(HttpResponseMessage response, string cookie)
        {
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"{cookie}; HttpOnly; Path=/; SameSite=Lax");
            return response;
        }

        private static Dictionary<string, string> ParseForm(string body) =>
            body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(
                    parts => Uri.UnescapeDataString(parts[0]),
                    parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);
    }
}
