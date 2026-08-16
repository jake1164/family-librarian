using FamilyLibrarian.Application.Accounts;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>
/// Populates the "oidc" scheme's options from <see cref="IOidcRuntimeSettingsCache"/>
/// every time they are resolved, rather than once at startup from configuration.
/// </summary>
/// <remarks>
/// ASP.NET Core's remote-authentication middleware resolves every registered
/// scheme's options on every request (to check whether the request matches its
/// callback path), including before an administrator has configured anything.
/// <see cref="OpenIdConnectPostConfigureOptions"/> (registered internally by
/// <c>AddOpenIdConnect</c>) throws unless at least one of Authority,
/// MetadataAddress, Configuration, or ConfigurationManager is set — so an
/// unconfigured deployment gets a harmless placeholder Authority
/// (<c>.invalid</c> is an IANA-reserved TLD that will never resolve) rather
/// than a real value. It is never actually fetched: the challenge/complete
/// endpoints refuse to use this scheme unless <see cref="OidcRuntimeSettings.IsUsable"/>.
/// <para>
/// Saving settings invalidates the cached options
/// (<see cref="Microsoft.Extensions.Options.IOptionsMonitorCache{TOptions}.TryRemove"/>),
/// which is what makes the next resolution re-run this and pick up new values —
/// see <c>OidcSettingsService</c> and its DI registration.
/// </para>
/// </remarks>
public sealed class OidcOptionsConfigurator(IOidcRuntimeSettingsCache cache)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string SchemeName = "oidc";
    private const string PlaceholderAuthority = "https://oidc.invalid";
    private const string PlaceholderClientId = "not-configured";
    private const string PlaceholderClientSecret = "not-configured";

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (!string.Equals(name, SchemeName, StringComparison.Ordinal))
        {
            return;
        }

        Configure(options);
    }

    public void Configure(OpenIdConnectOptions options)
    {
        var settings = cache.Current;

        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        // Deliberately left at the default (true): SignInManager's own
        // GetExternalLoginInfoAsync() hardcodes a lookup of the *mapped*
        // ClaimTypes.NameIdentifier to populate ExternalLoginInfo.ProviderKey,
        // so turning this off would silently break the subject/"sub" claim it
        // depends on. FindConfiguredClaimValue in Program.cs instead checks
        // both the raw short claim name and its mapped ClaimTypes equivalent,
        // so an admin-typed "email"/"groups" works whichever form is present.
        // OpenIdConnectOptions.Validate() — a separate built-in IValidateOptions
        // that AddOpenIdConnect registers automatically, distinct from
        // OpenIdConnectPostConfigureOptions above — unconditionally requires
        // ClientId and ClientSecret to be non-empty too, so an unconfigured
        // deployment needs placeholders for these exactly like Authority.
        // Never real values; the challenge/complete endpoints refuse to use
        // this scheme unless OidcRuntimeSettings.IsUsable is true.
        options.Authority = string.IsNullOrWhiteSpace(settings.Authority) ? PlaceholderAuthority : settings.Authority;
        options.ClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? PlaceholderClientId : settings.ClientId;
        options.ClientSecret = string.IsNullOrWhiteSpace(settings.ClientSecret) ? PlaceholderClientSecret : settings.ClientSecret;

        options.Scope.Clear();
        foreach (var scope in settings.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            options.Scope.Add(scope);
        }
    }
}
