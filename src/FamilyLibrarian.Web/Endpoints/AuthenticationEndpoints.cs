using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Web.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Web.Endpoints;

internal static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", LoginAsync)
            .AllowAnonymous();
        app.MapPost("/api/auth/logout", LogoutAsync)
            .RequireAuthorization();

        // OIDC. Anonymous by necessity — nobody is signed in yet — and reached by a
        // full browser navigation throughout (the login page renders these as plain
        // <a href> links, never a fetch), since the identity provider needs the
        // top-level browsing context to render its own sign-in UI.
        app.MapGet("/api/auth/oidc/status", GetOidcSignInStatusAsync)
            .AllowAnonymous();
        app.MapGet("/api/auth/oidc/challenge", ChallengeOidcAsync)
            .AllowAnonymous();
        app.MapGet("/api/auth/oidc/complete", CompleteOidcSignInAsync)
            .AllowAnonymous();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        IOidcRuntimeSettingsCache oidcCache,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FamilyLibrarian.Authentication");

        if (!new EmailAddressAttribute().IsValid(request.Email) || request.Password.Length < 8)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["Email is required."],
                ["password"] = ["Password is required."]
            });
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            AuthenticationLog.LoginUnknownAccount(logger, request.Email);
            return Results.Unauthorized();
        }

        // Checked before the password so a disabled account cannot be used as an
        // oracle for whether a password is still correct. The response is identical
        // to a wrong password either way.
        if (!UserStatuses.CanSignIn(user.Status))
        {
            AuthenticationLog.LoginDisabledAccount(logger, user.Id);
            return Results.Unauthorized();
        }

        // Local sign-in can be administratively disabled once OIDC is verified
        // working, but never for the one break-glass account — see OidcSettings'
        // own remarks on why a full OIDC-only mode isn't offered.
        if (oidcCache.Current.LocalLoginDisabled && !user.IsBreakGlass)
        {
            AuthenticationLog.LoginDisabledAccount(logger, user.Id);
            return Results.Unauthorized();
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            request.Password,
            request.RememberMe,
            lockoutOnFailure: true);

        if (result.Succeeded)
        {
            user.LastLoginAtUtc = DateTimeOffset.UtcNow;
            await userManager.UpdateAsync(user);
            AuthenticationLog.LoginSucceeded(logger, user.Id);
            return Results.NoContent();
        }

        if (result.IsLockedOut)
        {
            AuthenticationLog.LoginLockedOut(logger, user.Id);
        }
        else
        {
            AuthenticationLog.LoginInvalidPassword(logger, user.Id);
        }

        return Results.Unauthorized();
    }

    private static async Task<IResult> LogoutAsync(
        JsonElement requestBody,
        SignInManager<AppUser> signInManager)
    {
        _ = requestBody;
        await signInManager.SignOutAsync();
        return Results.NoContent();
    }

    private static IResult GetOidcSignInStatusAsync(IOidcRuntimeSettingsCache cache)
    {
        var settings = cache.Current;
        return Results.Ok(new OidcSignInStatusResponse(settings.IsUsable, settings.DisplayName, settings.LocalLoginDisabled));
    }

    private static IResult ChallengeOidcAsync(IOidcRuntimeSettingsCache cache)
    {
        if (!cache.Current.IsUsable)
        {
            return Results.NotFound();
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = "/api/auth/oidc/complete" },
            [OidcOptionsConfigurator.SchemeName]);
    }

    private static async Task<IResult> CompleteOidcSignInAsync(
        SignInManager<AppUser> signInManager,
        UserManager<AppUser> userManager,
        ExternalSignInService externalSignIn,
        IOidcRuntimeSettingsCache oidcCache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FamilyLibrarian.Authentication");
        var settings = oidcCache.Current;

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            AuthenticationLog.OidcCallbackWithoutTicket(logger);
            return Results.Redirect("/login?oidcError=failed");
        }

        var identity = new ExternalIdentity(
            Issuer: settings.Authority ?? OidcOptionsConfigurator.SchemeName,
            Subject: info.ProviderKey,
            Email: FindConfiguredClaimValue(info.Principal, settings.MatchClaimName),
            DisplayName: FindConfiguredClaimValue(info.Principal, "name") ?? FindConfiguredClaimValue(info.Principal, "email"),
            IsAdminClaimMatched: IsAdminClaimMatched(info.Principal, settings));

        var result = await externalSignIn.SignInAsync(identity, settings.AutoCreateAccounts, cancellationToken);

        // The External-scheme ticket has done its job (linking/provisioning read
        // it); it must not linger as a second, half-authenticated cookie.
        await signInManager.SignOutAsync();

        switch (result.Outcome)
        {
            case ExternalSignInOutcome.SignedIn:
                var user = await userManager.FindByIdAsync(result.UserId!.Value.ToString());
                if (user is null)
                {
                    return Results.Redirect("/login?oidcError=failed");
                }

                user.LastLoginAtUtc = DateTimeOffset.UtcNow;
                await userManager.UpdateAsync(user);
                await signInManager.SignInAsync(user, isPersistent: false);
                AuthenticationLog.OidcSignInSucceeded(logger, user.Id);
                return Results.Redirect("/");

            case ExternalSignInOutcome.NotActive:
                return Results.Redirect($"/login?oidcInfo={Uri.EscapeDataString(result.Message ?? string.Empty)}");

            default:
                AuthenticationLog.OidcSignInRejected(logger, result.Message ?? "unknown reason");
                return Results.Redirect($"/login?oidcError={Uri.EscapeDataString(result.Message ?? "rejected")}");
        }
    }

    private static bool IsAdminClaimMatched(ClaimsPrincipal principal, OidcRuntimeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AdminClaimName) || string.IsNullOrWhiteSpace(settings.AdminClaimValues))
        {
            return false;
        }

        var allowedValues = settings.AdminClaimValues.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return FindConfiguredClaimValues(principal, settings.AdminClaimName)
            .Any(value => allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A claim name an admin typed into settings ("email", "groups", "sub") may or
    /// may not have been remapped by ASP.NET Core's legacy inbound-claim-type
    /// mapping (see <c>OidcOptionsConfigurator</c>'s remarks) — this checks the
    /// raw short name first, then the well-known mapped equivalent, so either form
    /// of the token's claims resolves correctly.
    /// </summary>
    private static string? FindConfiguredClaimValue(ClaimsPrincipal principal, string claimName) =>
        principal.FindFirstValue(claimName) ??
        (OidcClaimAlias(claimName) is { } mapped ? principal.FindFirstValue(mapped) : null);

    private static IEnumerable<string> FindConfiguredClaimValues(ClaimsPrincipal principal, string claimName)
    {
        var values = principal.FindAll(claimName).Select(claim => claim.Value);
        if (OidcClaimAlias(claimName) is { } mapped)
        {
            values = values.Concat(principal.FindAll(mapped).Select(claim => claim.Value));
        }

        return values.Distinct();
    }

    private static string? OidcClaimAlias(string claimName) => claimName.ToLowerInvariant() switch
    {
        "sub" => ClaimTypes.NameIdentifier,
        "email" => ClaimTypes.Email,
        "name" => ClaimTypes.Name,
        "given_name" => ClaimTypes.GivenName,
        "family_name" => ClaimTypes.Surname,
        "role" => ClaimTypes.Role,
        _ => null
    };
}
