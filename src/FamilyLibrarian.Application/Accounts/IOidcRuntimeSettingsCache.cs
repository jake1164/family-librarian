namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// The decrypted OIDC configuration the running host currently uses, held
/// in memory so the OIDC authentication handler never blocks on a database
/// call while resolving its options.
/// </summary>
/// <remarks>
/// Populated once at startup and refreshed synchronously by
/// <see cref="OidcSettingsService"/> immediately after every save — there is
/// no polling and no restart involved in picking up a change.
/// </remarks>
public interface IOidcRuntimeSettingsCache
{
    OidcRuntimeSettings Current { get; }

    void Refresh(OidcRuntimeSettings settings);
}

public sealed record OidcRuntimeSettings(
    bool IsEnabled,
    string DisplayName,
    string? Authority,
    string? ClientId,
    string? ClientSecret,
    string Scopes,
    string MatchClaimName,
    string? AdminClaimName,
    string? AdminClaimValues,
    bool AutoCreateAccounts,
    bool LocalLoginDisabled)
{
    public static readonly OidcRuntimeSettings Disabled = new(
        IsEnabled: false,
        DisplayName: "Sign in with SSO",
        Authority: null,
        ClientId: null,
        ClientSecret: null,
        Scopes: "openid profile email",
        MatchClaimName: "email",
        AdminClaimName: "groups",
        AdminClaimValues: null,
        AutoCreateAccounts: false,
        LocalLoginDisabled: false);

    /// <summary>Whether the handler has enough to actually attempt a challenge.</summary>
    public bool IsUsable =>
        IsEnabled && !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
