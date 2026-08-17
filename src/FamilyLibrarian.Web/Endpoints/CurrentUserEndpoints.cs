using System.Security.Claims;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// The small set of routes about the caller themselves rather than any feature
/// area: who am I, a request token to mutate with, and an authorization probe.
/// </summary>
internal static class CurrentUserEndpoints
{
    public static void MapCurrentUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/antiforgery/token", AntiforgeryTokenEndpoint.GetToken)
            .RequireAuthorization();

        app.MapGet("/api/v1/me", GetCurrentUserAsync)
            .RequireAuthorization();
        app.MapGet("/api/v1/admin/ping", () => Results.Ok(new { status = "ok" }))
            .RequireAuthorization("Admin");
    }

    private static async Task<IResult> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);
        return Results.Ok(new CurrentUserResponse(
            user.Id,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? user.Email ?? "Family Librarian user" : user.DisplayName,
            user.Email,
            roles.ToArray()));
    }
}
