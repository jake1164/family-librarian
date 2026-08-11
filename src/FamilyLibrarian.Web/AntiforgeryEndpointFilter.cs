using Microsoft.AspNetCore.Antiforgery;

namespace FamilyLibrarian.Web;

/// <summary>
/// Requires a valid anti-forgery token on state-changing requests.
/// </summary>
/// <remarks>
/// The host authenticates with a cookie, so the browser attaches credentials to
/// any cross-site request it can be tricked into issuing. ASP.NET Core's built-in
/// anti-forgery middleware only auto-validates form posts, and these endpoints
/// take JSON — so the check is applied explicitly here, as
/// <c>docs/03-provider-api-contracts.md</c> requires for enable, disable, create,
/// replace, clear, and test-connection.
/// <para>
/// Safe methods pass through untouched; only requests that change state are
/// validated.
/// </para>
/// </remarks>
public sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (HttpMethods.IsGet(httpContext.Request.Method) ||
            HttpMethods.IsHead(httpContext.Request.Method) ||
            HttpMethods.IsOptions(httpContext.Request.Method) ||
            HttpMethods.IsTrace(httpContext.Request.Method))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            // Deliberately terse: the reason a token failed is not useful to a
            // legitimate client and is a hint to an attacker.
            return Results.Problem(
                title: "Invalid anti-forgery token",
                detail: "Refresh the page and try again.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
