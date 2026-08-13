namespace FamilyLibrarian.Web;

public static class InvitationEndpoints
{
    public const string RateLimitPolicy = "invitation-redemption";

    /// <summary>The client route that redeems an invitation.</summary>
    public const string RedeemPath = "/invite";

    /// <summary>
    /// Builds the link an administrator hands to the invitee.
    /// </summary>
    /// <remarks>
    /// The token travels in the URL <em>fragment</em>, not the path or query.
    /// Browsers never send a fragment to the server, so the token stays out of
    /// access logs, proxy logs, and <c>Referer</c> headers on the page the
    /// invitee lands on — while the WebAssembly client can still read it. The
    /// token only reaches the host in the body of the redemption POST.
    /// </remarks>
    public static string BuildRedeemUrl(HttpContext httpContext, string token)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var request = httpContext.Request;
        var origin = $"{request.Scheme}://{request.Host}{request.PathBase}";
        return $"{origin}{RedeemPath}#{token}";
    }
}
