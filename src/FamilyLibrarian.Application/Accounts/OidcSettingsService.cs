using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Accounts;

/// <summary>The administrative commands behind the OIDC settings panel, mirroring <c>CwaSettingsService</c>'s shape.</summary>
public sealed class OidcSettingsService(
    IOidcSettingsStore store,
    ICredentialProtector protector,
    IOidcDiscoveryTester discoveryTester,
    IOidcRuntimeSettingsCache runtimeCache,
    IOidcOptionsInvalidator optionsInvalidator,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<OidcStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return ToStatus(settings);
    }

    /// <summary>
    /// Loads the current settings into the shape the OIDC handler needs, for the
    /// one-time bootstrap read at startup (<c>InitializeOidcRuntimeCacheAsync</c>).
    /// Every save refreshes the cache itself; this is only for the cold start.
    /// </summary>
    public async Task<OidcRuntimeSettings> LoadRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return settings is null ? OidcRuntimeSettings.Disabled : ToRuntimeSettings(settings);
    }

    public async Task<OidcCommandResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(
            isEnabled ? AuditActions.OidcEnabled : AuditActions.OidcDisabled,
            AuditSubjectTypes.Oidc, null, new { Enabled = isEnabled }, cancellationToken);

        return OidcCommandResult.Success(ToStatus(settings));
    }

    public async Task<OidcCommandResult> SetSettingsAsync(
        string displayName,
        string? authority,
        string? clientId,
        string scopes,
        string matchClaimName,
        string? adminClaimName,
        string? adminClaimValues,
        bool autoCreateAccounts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return OidcCommandResult.Invalid("A button label is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(
            displayName, authority, clientId, scopes, matchClaimName, adminClaimName, adminClaimValues,
            autoCreateAccounts, currentUser.UserId, clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(
            AuditActions.OidcSettingsChanged, AuditSubjectTypes.Oidc, null, new { }, cancellationToken);

        return OidcCommandResult.Success(ToStatus(settings));
    }

    public async Task<OidcCommandResult> SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken)
    {
        var trimmed = clientSecret?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return OidcCommandResult.Invalid("A client secret is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetClientSecret(
            protector.Protect(AuthenticationSecretPurposes.OidcClientSecret, trimmed),
            protector.FormatVersion,
            BuildHint(trimmed),
            currentUser.UserId,
            clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(AuditActions.OidcSecretSet, AuditSubjectTypes.Oidc, null, new { }, cancellationToken);

        return OidcCommandResult.Success(ToStatus(settings));
    }

    public async Task<OidcCommandResult> ClearClientSecretAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearClientSecret(currentUser.UserId, clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(AuditActions.OidcSecretCleared, AuditSubjectTypes.Oidc, null, new { }, cancellationToken);

        return OidcCommandResult.Success(ToStatus(settings));
    }

    public async Task<OidcCommandResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        var outcome = await discoveryTester.TestAsync(settings.Authority, cancellationToken);

        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.OidcTested, AuditSubjectTypes.Oidc, null, new { outcome.Succeeded }, cancellationToken);

        return OidcCommandResult.Success(ToStatus(settings), outcome.Endpoints);
    }

    public async Task<OidcCommandResult> SetLocalLoginDisabledAsync(bool disabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);

        try
        {
            settings.SetLocalLoginDisabled(disabled, currentUser.UserId, clock.UtcNow);
        }
        catch (InvalidOperationException exception)
        {
            return OidcCommandResult.Invalid(exception.Message);
        }

        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(
            AuditActions.OidcLocalLoginChanged, AuditSubjectTypes.Oidc, null,
            new { LocalLoginDisabled = disabled }, cancellationToken);

        return OidcCommandResult.Success(ToStatus(settings));
    }

    private async Task SaveAndRefreshAsync(OidcSettings settings, CancellationToken cancellationToken)
    {
        await store.SaveChangesAsync(cancellationToken);
        runtimeCache.Refresh(ToRuntimeSettings(settings));
        optionsInvalidator.Invalidate();
    }

    private OidcRuntimeSettings ToRuntimeSettings(OidcSettings settings) => new(
        settings.IsEnabled,
        settings.DisplayName,
        settings.Authority,
        settings.ClientId,
        settings.HasClientSecret
            ? protector.Unprotect(
                AuthenticationSecretPurposes.OidcClientSecret,
                settings.ProtectedClientSecret!,
                settings.ClientSecretFormatVersion)
            : null,
        settings.Scopes,
        settings.MatchClaimName,
        settings.AdminClaimName,
        settings.AdminClaimValues,
        settings.AutoCreateAccounts,
        settings.LocalLoginDisabled);

    private static OidcStatus ToStatus(OidcSettings? settings) => settings is null
        ? new OidcStatus(false, "Sign in with SSO", null, null, false, null, null, "openid profile email",
            "email", "groups", null, false, false, null, null, null)
        : new OidcStatus(
            settings.IsEnabled,
            settings.DisplayName,
            settings.Authority,
            settings.ClientId,
            settings.HasClientSecret,
            settings.ClientSecretHint,
            settings.ClientSecretSetAtUtc,
            settings.Scopes,
            settings.MatchClaimName,
            settings.AdminClaimName,
            settings.AdminClaimValues,
            settings.AutoCreateAccounts,
            settings.LocalLoginDisabled,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? BuildHint(string value) => value.Length <= 4 ? null : value[^4..];
}

/// <summary>Fetches an issuer's OIDC discovery document, purely to confirm it is reachable and well-formed.</summary>
public interface IOidcDiscoveryTester
{
    Task<OidcDiscoveryTestOutcome> TestAsync(string? authority, CancellationToken cancellationToken);
}

public sealed record OidcDiscoveryTestOutcome(bool Succeeded, string Message, OidcDiscoveryEndpoints? Endpoints)
{
    public static OidcDiscoveryTestOutcome Success(OidcDiscoveryEndpoints endpoints) =>
        new(true, "The issuer's discovery document was found.", endpoints);

    public static OidcDiscoveryTestOutcome Failure(string message) => new(false, message, null);
}

public sealed record OidcDiscoveryEndpoints(
    string? AuthorizationEndpoint, string? TokenEndpoint, string? UserinfoEndpoint, string? JwksUri, string? EndSessionEndpoint);

public sealed record OidcStatus(
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

public sealed record OidcCommandResult(
    OidcCommandOutcome Outcome, OidcStatus? Status, string? Error, OidcDiscoveryEndpoints? DiscoveryEndpoints = null)
{
    public static OidcCommandResult Success(OidcStatus status, OidcDiscoveryEndpoints? endpoints = null) =>
        new(OidcCommandOutcome.Success, status, null, endpoints);

    public static OidcCommandResult Invalid(string error) => new(OidcCommandOutcome.Invalid, null, error);
}

public enum OidcCommandOutcome
{
    Success,
    Invalid
}
