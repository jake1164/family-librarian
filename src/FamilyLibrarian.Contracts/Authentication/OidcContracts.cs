namespace FamilyLibrarian.Contracts.Authentication;

/// <summary>What the login page needs to decide whether to show an OIDC button.</summary>
public sealed record OidcSignInStatusResponse(bool Enabled, string DisplayName, bool LocalLoginDisabled);

public sealed record OidcSettingsResponse(
    bool IsEnabled,
    string DisplayName,
    string? Authority,
    string? ClientId,
    bool HasClientSecret,
    string? ClientSecretHint,
    DateTimeOffset? ClientSecretSetAtUtc,
    string Scopes,
    string MatchClaimName,
    string? AdminClaimName,
    string? AdminClaimValues,
    bool AutoCreateAccounts,
    bool LocalLoginDisabled,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record SetOidcSettingsRequest(
    string DisplayName,
    string? Authority,
    string? ClientId,
    string Scopes,
    string MatchClaimName,
    string? AdminClaimName,
    string? AdminClaimValues,
    bool AutoCreateAccounts);

public sealed record SetOidcEnabledRequest(bool Enabled);

public sealed record SetOidcClientSecretRequest(string ClientSecret);

public sealed record SetOidcLocalLoginDisabledRequest(bool Disabled);

/// <summary>
/// A non-persistent discovery-document probe against the issuer URL currently
/// in the administrator's form, which may not be saved yet. A blank authority
/// falls back to whatever is currently saved.
/// </summary>
public sealed record TestOidcConnectionRequest(string? Authority);

public sealed record OidcConnectionTestResponse(
    bool Succeeded,
    string Message,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? UserinfoEndpoint,
    string? JwksUri,
    string? EndSessionEndpoint);
