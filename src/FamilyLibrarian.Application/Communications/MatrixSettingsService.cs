using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>Administrative commands for the optional outbound Matrix provider (COMM-1 §B).</summary>
public sealed class MatrixSettingsService(
    IMatrixSettingsStore store,
    ICredentialProtector protector,
    IMatrixClient matrixClient,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string ProviderId = "matrix";

    public async Task<MatrixStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        ToStatus(await store.FindAsync(cancellationToken));

    public async Task<MatrixCommandResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (enabled && GetConfigurationError(settings) is { } error)
        {
            return MatrixCommandResult.Invalid(error);
        }

        settings.SetEnabled(enabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            enabled ? AuditActions.CommunicationProviderEnabled : AuditActions.CommunicationProviderDisabled,
            AuditSubjectTypes.CommunicationProvider,
            ProviderId,
            new { Provider = ProviderId, Enabled = enabled },
            cancellationToken);

        return MatrixCommandResult.Success(ToStatus(settings));
    }

    public async Task<MatrixCommandResult> SetSettingsAsync(
        string? homeserverUrl, string? botUserId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(homeserverUrl))
        {
            if (!Uri.TryCreate(homeserverUrl.Trim(), UriKind.Absolute, out var uri))
            {
                return MatrixCommandResult.Invalid("The Matrix homeserver URL is not valid.");
            }

            if (uri.Scheme is not ("http" or "https"))
            {
                return MatrixCommandResult.Invalid("The Matrix homeserver URL must use http or https.");
            }
        }

        if (!string.IsNullOrWhiteSpace(botUserId) && !botUserId.Trim().StartsWith('@'))
        {
            return MatrixCommandResult.Invalid("The bot's Matrix user ID must look like @user:example.org.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(homeserverUrl, botUserId, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSettingsChanged,
            AuditSubjectTypes.CommunicationProvider,
            ProviderId,
            new { Provider = ProviderId },
            cancellationToken);

        return MatrixCommandResult.Success(ToStatus(settings));
    }

    public async Task<MatrixCommandResult> SetAccessTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        var trimmed = accessToken?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return MatrixCommandResult.Invalid("A Matrix access token is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetAccessToken(
            protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, trimmed),
            protector.FormatVersion,
            currentUser.UserId,
            clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSecretSet,
            AuditSubjectTypes.CommunicationProvider,
            ProviderId,
            new { Provider = ProviderId, Field = "access_token" },
            cancellationToken);

        return MatrixCommandResult.Success(ToStatus(settings));
    }

    public async Task<MatrixCommandResult> ClearAccessTokenAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearAccessToken(currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderSecretCleared,
            AuditSubjectTypes.CommunicationProvider,
            ProviderId,
            new { Provider = ProviderId, Field = "access_token" },
            cancellationToken);

        return MatrixCommandResult.Success(ToStatus(settings));
    }

    /// <summary>
    /// Tests the values currently in the administrator's form -- including a
    /// freshly typed but unsaved access token -- without changing the
    /// persisted homeserver URL/bot user ID. Mirrors <c>SmtpSettingsService.SendTestAsync</c>:
    /// any omitted field falls back to the currently saved setting, and only
    /// the pass/fail outcome is recorded onto the persisted settings.
    /// </summary>
    public async Task<MatrixTestResult> SendTestAsync(
        string? homeserverUrl, string? botUserId, string? accessToken, CancellationToken cancellationToken)
    {
        var persisted = await store.FindAsync(cancellationToken);
        var candidate = new MatrixSettings(clock.UtcNow);
        candidate.SetSettings(
            homeserverUrl ?? persisted?.HomeserverUrl, botUserId ?? persisted?.BotUserId, currentUser.UserId, clock.UtcNow);

        var trimmedToken = accessToken?.Trim();
        string? plaintextToken;
        if (!string.IsNullOrEmpty(trimmedToken))
        {
            plaintextToken = trimmedToken;
            candidate.SetAccessToken(
                protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, trimmedToken),
                protector.FormatVersion,
                currentUser.UserId,
                clock.UtcNow);
        }
        else if (persisted?.HasAccessToken == true)
        {
            plaintextToken = protector.Unprotect(
                CommunicationSecretPurposes.MatrixAccessToken, persisted.ProtectedAccessToken!, persisted.AccessTokenFormatVersion);
            candidate.SetAccessToken(
                persisted.ProtectedAccessToken!, persisted.AccessTokenFormatVersion, currentUser.UserId, clock.UtcNow);
        }
        else
        {
            plaintextToken = null;
        }

        if (GetConnectionPrerequisiteError(candidate) is { } error)
        {
            return MatrixTestResult.Invalid(error);
        }

        if (plaintextToken is null)
        {
            return MatrixTestResult.Invalid("The stored Matrix access token could not be decrypted.");
        }

        var outcome = await matrixClient.TestConnectionAsync(candidate, plaintextToken, cancellationToken);

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.CommunicationProviderTested,
            AuditSubjectTypes.CommunicationProvider,
            ProviderId,
            new { Provider = ProviderId, outcome.Succeeded },
            cancellationToken);

        return MatrixTestResult.Success(ToStatus(settings), outcome);
    }

    private static MatrixStatus ToStatus(MatrixSettings? settings) => settings is null
        ? new MatrixStatus(false, null, null, false, null, null, null, null)
        : new MatrixStatus(
            settings.IsEnabled,
            settings.HomeserverUrl,
            settings.BotUserId,
            settings.HasAccessToken,
            settings.AccessTokenSetAtUtc,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? GetConfigurationError(MatrixSettings settings)
    {
        if (GetConnectionPrerequisiteError(settings) is { } error)
        {
            return error;
        }

        if (settings.LastTestSucceeded != true)
        {
            return "Send a successful test message for the currently saved configuration before enabling Matrix.";
        }

        return null;
    }

    private static string? GetConnectionPrerequisiteError(MatrixSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.HomeserverUrl)) return "A Matrix homeserver URL is required.";
        if (!settings.HasAccessToken) return "A Matrix bot access token is required.";
        return null;
    }
}

public sealed record MatrixCommandResult(bool Succeeded, MatrixStatus? Status, string? Error)
{
    public static MatrixCommandResult Success(MatrixStatus status) => new(true, status, null);

    public static MatrixCommandResult Invalid(string error) => new(false, null, error);
}

public sealed record MatrixTestResult(bool Succeeded, MatrixStatus? Status, ConnectionTestOutcome? Outcome, string? Error)
{
    public static MatrixTestResult Success(MatrixStatus status, ConnectionTestOutcome outcome) => new(true, status, outcome, null);

    public static MatrixTestResult Invalid(string error) => new(false, null, null, error);
}
