namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// Fixed <see cref="Integrations.ICredentialProtector"/> purpose string for the
/// OIDC client secret, shared between <see cref="OidcSettingsService"/> (which
/// writes it) and whatever decrypts it at token-exchange time.
/// </summary>
public static class AuthenticationSecretPurposes
{
    public const string OidcClientSecret = "oidc-client-secret";
}
