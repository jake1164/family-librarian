namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// Forces the next resolution of the "oidc" authentication scheme's options to
/// re-run its configurator against fresh settings.
/// </summary>
/// <remarks>
/// <see cref="IOidcRuntimeSettingsCache"/> alone is not enough: ASP.NET Core's
/// options system caches the *materialized* <c>OpenIdConnectOptions</c> per
/// scheme name indefinitely once first resolved, so without this, only the
/// very first request after boot would ever see a saved change.
/// </remarks>
public interface IOidcOptionsInvalidator
{
    void Invalidate();
}
