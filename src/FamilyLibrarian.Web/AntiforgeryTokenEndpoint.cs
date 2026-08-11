using Microsoft.AspNetCore.Antiforgery;

namespace FamilyLibrarian.Web;

public static class AntiforgeryTokenEndpoint
{
    public const string HeaderName = "X-CSRF-TOKEN";

    /// <summary>
    /// Deliberately not prefixed with <c>__Host-</c>. That prefix obliges the
    /// client to reject any cookie lacking <c>Secure</c>, and the default local
    /// and Compose topologies serve plain HTTP on <c>localhost:8080</c> — the
    /// cookie would be silently dropped and every state change would fail
    /// anti-forgery validation. The cookie still carries <c>HttpOnly</c> and
    /// <c>SameSite=Strict</c>, and picks up <c>Secure</c> automatically once the
    /// request arrives over HTTPS.
    /// </summary>
    public const string CookieName = "fl-antiforgery";

    /// <summary>
    /// Issues a request token and sets its paired cookie.
    /// </summary>
    /// <remarks>
    /// Anti-forgery works on the double-submit principle: the cookie is set here
    /// and the caller must echo the matching token in
    /// <see cref="HeaderName"/>. A cross-site page can cause the browser to send
    /// the cookie but cannot read this response to learn the header value, so it
    /// cannot forge a valid pair.
    /// <para>
    /// The endpoint requires authentication so a token is only minted for a
    /// caller that already has a session.
    /// </para>
    /// </remarks>
    public static IResult GetToken(HttpContext httpContext, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);

        return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken ?? string.Empty));
    }
}

public sealed record AntiforgeryTokenResponse(string Token);
