namespace FamilyLibrarian.Web;

/// <summary>
/// Response headers docs/01 §13.1's baseline hardening list named but nothing
/// set — see F7 in the architecture review.
/// </summary>
/// <remarks>
/// The Content-Security-Policy ships in <em>report-only</em> mode, not
/// enforcing: this is a client-side Blazor WebAssembly SPA using MudBlazor,
/// and MudBlazor's runtime style injection has not been checked against an
/// enforcing policy in a real browser. Report-only can only log a violation,
/// never block one, so it is safe to ship immediately; promoting it to
/// enforcing needs a browser session watching the console for violations
/// first. The policy string itself matches Microsoft's documented starting
/// point for a client-side Blazor app, plus <c>frame-ancestors 'none'</c> —
/// see https://learn.microsoft.com/aspnet/core/blazor/security/content-security-policy.
/// <para>
/// The other headers here carry no such risk and ship enforcing:
/// <c>X-Content-Type-Options</c>, <c>Referrer-Policy</c>, and
/// <c>X-Frame-Options</c> (the older, universally-supported clickjacking
/// header — kept alongside the CSP's own <c>frame-ancestors</c> directive so
/// framing is actually blocked today rather than only reported).
/// </para>
/// </remarks>
public static class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "base-uri 'self'; " +
        "default-src 'self'; " +
        "img-src 'self' data: https:; " +
        "object-src 'none'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "upgrade-insecure-requests";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.Append("X-Content-Type-Options", "nosniff");
            headers.Append("X-Frame-Options", "DENY");
            headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            headers.Append("Content-Security-Policy-Report-Only", ContentSecurityPolicy);

            await next(context);
        });
}
