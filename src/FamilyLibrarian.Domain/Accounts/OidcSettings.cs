namespace FamilyLibrarian.Domain.Accounts;

/// <summary>
/// Administrator-managed configuration for the optional generic OIDC sign-in.
/// </summary>
/// <remarks>
/// One row, created on first configuration — same shape as <c>CwaSettings</c>.
/// Only the issuer's Authority URL is stored; the authorize/token/userinfo/JWKS
/// endpoints are resolved live by the OIDC handler from the issuer's own
/// discovery document, not persisted here.
/// </remarks>
public sealed class OidcSettings
{
    private OidcSettings()
    {
    }

    public OidcSettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        IsEnabled = false;
        DisplayName = "Sign in with SSO";
        Scopes = "openid profile email";
        MatchClaimName = "email";
        AdminClaimName = "groups";
        AutoCreateAccounts = false;
        LocalLoginDisabled = false;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    public string DisplayName { get; private set; } = "Sign in with SSO";

    public string? Authority { get; private set; }

    public string? ClientId { get; private set; }

    public string? ProtectedClientSecret { get; private set; }

    public int ClientSecretFormatVersion { get; private set; }

    public string? ClientSecretHint { get; private set; }

    public DateTimeOffset? ClientSecretSetAtUtc { get; private set; }

    public string Scopes { get; private set; } = "openid profile email";

    /// <summary>Which claim links an external identity to an existing local account.</summary>
    public string MatchClaimName { get; private set; } = "email";

    /// <summary>Which claim, if any, carries group/role membership. Never a hardcoded IdP concept.</summary>
    public string? AdminClaimName { get; private set; } = "groups";

    /// <summary>Comma-separated claim values that grant the internal Admin role.</summary>
    public string? AdminClaimValues { get; private set; }

    /// <summary>
    /// Whether an unrecognized external identity becomes <see cref="UserStatus.Active"/>
    /// immediately, or <see cref="UserStatus.PendingApproval"/> pending an administrator.
    /// </summary>
    public bool AutoCreateAccounts { get; private set; }

    /// <summary>
    /// Blocks local sign-in for every account except one flagged
    /// <c>AppUser.IsBreakGlass</c>. Only settable once <see cref="LastTestSucceeded"/> is true.
    /// </summary>
    public bool LocalLoginDisabled { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasClientSecret => !string.IsNullOrEmpty(ProtectedClientSecret);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetSettings(
        string displayName,
        string? authority,
        string? clientId,
        string scopes,
        string matchClaimName,
        string? adminClaimName,
        string? adminClaimValues,
        bool autoCreateAccounts,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        DisplayName = RequireText(displayName, nameof(displayName));
        Authority = Trim(authority);
        ClientId = Trim(clientId);
        Scopes = string.IsNullOrWhiteSpace(scopes) ? "openid profile email" : scopes.Trim();
        MatchClaimName = string.IsNullOrWhiteSpace(matchClaimName) ? "email" : matchClaimName.Trim();
        AdminClaimName = Trim(adminClaimName);
        AdminClaimValues = Trim(adminClaimValues);
        AutoCreateAccounts = autoCreateAccounts;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetClientSecret(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedClientSecret = protectedValue;
        ClientSecretFormatVersion = formatVersion;
        ClientSecretHint = hint;
        ClientSecretSetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearClientSecret(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedClientSecret = null;
        ClientSecretFormatVersion = 0;
        ClientSecretHint = null;
        ClientSecretSetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void RecordTestResult(bool succeeded, string? message, Guid? actorUserId, DateTimeOffset testedAtUtc)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = Truncate(message, 512);
        Touch(actorUserId, testedAtUtc);
    }

    /// <exception cref="InvalidOperationException">No successful connection test is on record.</exception>
    public void SetLocalLoginDisabled(bool disabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (disabled && LastTestSucceeded != true)
        {
            throw new InvalidOperationException(
                "Local sign-in cannot be disabled until a connection test has succeeded.");
        }

        LocalLoginDisabled = disabled;
        Touch(actorUserId, updatedAtUtc);
    }

    private void ResetTestResult()
    {
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
