using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>The administrative commands behind the Audiobookshelf settings panel, mirroring <c>ProviderAdminService</c>'s shape.</summary>
public sealed class AudiobookshelfSettingsService(
    IAudiobookshelfSettingsStore store,
    ICredentialProtector protector,
    IAudiobookshelfConnectionTester connectionTester,
    IAudiobookshelfLibraryDiscoveryClient libraryDiscoveryClient,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string ApiTokenPurpose = PublishingSecretPurposes.AudiobookshelfApiToken;

    public async Task<AudiobookshelfStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return ToStatus(settings);
    }

    /// <summary>
    /// Whether a user may request an Audiobook right now. Mirrors
    /// <c>CwaSettingsService.GetRequestReadinessErrorAsync</c>'s shape and bar
    /// (enabled, fully configured, and a passing test for the currently saved
    /// configuration). Settings can change after enabling, so readiness checks
    /// again. Returns the reason when not ready, or <see langword="null"/> when ready.
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        (await store.FindAsync(cancellationToken))?.IsEnabled == true;

    public async Task<string?> GetRequestReadinessErrorAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            return "Audiobookshelf is not enabled.";
        }

        return GetConfigurationError(settings);
    }

    public async Task<AudiobookshelfCommandResult> SetEnabledAsync(
        bool isEnabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (isEnabled)
        {
            if (GetRequirementError(settings) is { } requirementError)
            {
                return AudiobookshelfCommandResult.Invalid(requirementError);
            }

            // Requests need a passing test of the saved settings, so an enabled destination without one would be unusable.
            if (settings.LastTestSucceeded != true)
            {
                var test = await TestConnectionAsync(cancellationToken);
                if (!test.Outcome.Succeeded)
                {
                    return AudiobookshelfCommandResult.Invalid(
                        $"Audiobookshelf stayed off. The connection test failed: {test.Outcome.Message}");
                }
            }
        }

        settings.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            isEnabled ? AuditActions.PublishingDestinationEnabled : AuditActions.PublishingDestinationDisabled,
            AuditSubjectTypes.PublishingDestination,
            "audiobookshelf",
            new { Destination = "audiobookshelf", Enabled = isEnabled },
            cancellationToken);

        return AudiobookshelfCommandResult.Success(ToStatus(settings));
    }

    public async Task<AudiobookshelfCommandResult> SetSettingsAsync(
        string? baseUrl, string? publicUrl, string? libraryId, string? folderId, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(baseUrl, publicUrl, libraryId, folderId, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSettingsChanged,
            AuditSubjectTypes.PublishingDestination,
            "audiobookshelf",
            new { Destination = "audiobookshelf" },
            cancellationToken);

        return AudiobookshelfCommandResult.Success(ToStatus(settings));
    }

    public async Task<AudiobookshelfCommandResult> SetApiTokenAsync(
        string apiToken, CancellationToken cancellationToken)
    {
        var trimmed = apiToken?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return AudiobookshelfCommandResult.Invalid("An API token is required.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetApiToken(
            protector.Protect(ApiTokenPurpose, trimmed),
            protector.FormatVersion,
            BuildHint(trimmed),
            currentUser.UserId,
            clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSecretSet,
            AuditSubjectTypes.PublishingDestination,
            "audiobookshelf",
            new { Destination = "audiobookshelf", Field = ApiTokenPurpose },
            cancellationToken);

        return AudiobookshelfCommandResult.Success(ToStatus(settings));
    }

    public async Task<AudiobookshelfCommandResult> ClearApiTokenAsync(
        CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearApiToken(currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationSecretCleared,
            AuditSubjectTypes.PublishingDestination,
            "audiobookshelf",
            new { Destination = "audiobookshelf" },
            cancellationToken);

        return AudiobookshelfCommandResult.Success(ToStatus(settings));
    }

    /// <summary>
    /// Tests the currently *persisted* configuration and records the result,
    /// mirroring <c>CwaSettingsService.TestConnectionAsync</c>. Unlike
    /// <see cref="TestConfigurationAsync"/>, this is what actually lets
    /// <c>LastTestSucceeded</c> become true — required for the format-readiness
    /// gate, since nothing else in this service ever records a test result.
    /// </summary>
    public async Task<AudiobookshelfConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        var outcome = await connectionTester.TestAsync(settings, cancellationToken);
        await RecordTestAsync(settings, outcome, cancellationToken);

        return new AudiobookshelfConnectionTestResult(ToStatus(settings), outcome);
    }

    private async Task RecordTestAsync(
        AudiobookshelfSettings settings, ConnectionTestOutcome outcome, CancellationToken cancellationToken)
    {
        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationTested,
            AuditSubjectTypes.PublishingDestination,
            "audiobookshelf",
            new { Destination = "audiobookshelf", outcome.Succeeded },
            cancellationToken);
    }

    /// <summary>
    /// Tests the base URL, library ID, folder ID, and API token currently in
    /// the administrator's form without changing persisted configuration or the
    /// stored token. The result is recorded only when those values are the
    /// saved ones; a test of unsaved values must never mark the saved settings
    /// as tested. A blank <paramref name="apiToken"/> falls back to the stored
    /// token so an administrator can test a base URL/library/folder change
    /// without retyping a token they already saved.
    /// </summary>
    public async Task<ConnectionTestOutcome> TestConfigurationAsync(
        string? baseUrl, string? libraryId, string? folderId, string? apiToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ConnectionTestOutcome(false, "No base URL is configured.");
        }

        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return new ConnectionTestOutcome(false, "No library ID is configured.");
        }

        var persisted = await store.FindAsync(cancellationToken);
        var candidate = new AudiobookshelfSettings(clock.UtcNow);
        candidate.SetSettings(baseUrl, null, libraryId, folderId, currentUser.UserId, clock.UtcNow);

        var trimmedToken = apiToken?.Trim();
        if (!string.IsNullOrEmpty(trimmedToken))
        {
            candidate.SetApiToken(
                protector.Protect(ApiTokenPurpose, trimmedToken),
                protector.FormatVersion,
                BuildHint(trimmedToken),
                currentUser.UserId,
                clock.UtcNow);
        }
        else if (persisted?.HasApiToken == true)
        {
            candidate.SetApiToken(
                persisted.ProtectedApiToken!,
                persisted.ApiTokenFormatVersion,
                persisted.ApiTokenHint,
                currentUser.UserId,
                clock.UtcNow);
        }

        var outcome = await connectionTester.TestAsync(candidate, cancellationToken);
        if (persisted is not null && MatchesSavedConfiguration(persisted, candidate))
        {
            await RecordTestAsync(await store.GetOrCreateAsync(cancellationToken), outcome, cancellationToken);
            return outcome;
        }

        return outcome with { Message = $"{outcome.Message} These values are not saved yet -- click Save settings to keep them." };
    }

    private bool MatchesSavedConfiguration(AudiobookshelfSettings persisted, AudiobookshelfSettings candidate) =>
        persisted.HasApiToken
        && candidate.HasApiToken
        && string.Equals(persisted.BaseUrl, candidate.BaseUrl, StringComparison.Ordinal)
        && string.Equals(persisted.LibraryId, candidate.LibraryId, StringComparison.Ordinal)
        && string.Equals(persisted.FolderId, candidate.FolderId, StringComparison.Ordinal)
        && string.Equals(
            protector.Unprotect(ApiTokenPurpose, persisted.ProtectedApiToken!, persisted.ApiTokenFormatVersion),
            protector.Unprotect(ApiTokenPurpose, candidate.ProtectedApiToken!, candidate.ApiTokenFormatVersion),
            StringComparison.Ordinal);

    /// <summary>
    /// Lists the libraries (and their folders) on the Audiobookshelf instance
    /// at <paramref name="baseUrl"/>, so the settings UI can offer them as a
    /// pick-list instead of the administrator having to find raw IDs
    /// elsewhere. A blank <paramref name="apiToken"/> falls back to the
    /// stored token.
    /// </summary>
    public async Task<AudiobookshelfLibraryDiscoveryOutcome> DiscoverLibrariesAsync(
        string? baseUrl, string? apiToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return AudiobookshelfLibraryDiscoveryOutcome.Failure("A base URL is required.");
        }

        var token = apiToken?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            var persisted = await store.FindAsync(cancellationToken);
            if (persisted?.HasApiToken == true)
            {
                token = protector.Unprotect(
                    ApiTokenPurpose, persisted.ProtectedApiToken!, persisted.ApiTokenFormatVersion);
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            return AudiobookshelfLibraryDiscoveryOutcome.Failure("An API token is required.");
        }

        return await libraryDiscoveryClient.ListLibrariesAsync(baseUrl.Trim(), token, cancellationToken);
    }

    private static AudiobookshelfStatus ToStatus(AudiobookshelfSettings? settings) => settings is null
        ? new AudiobookshelfStatus(false, null, null, null, null, false, null, null, null, null, null)
        : new AudiobookshelfStatus(
            settings.IsEnabled,
            settings.BaseUrl,
            settings.PublicUrl,
            settings.LibraryId,
            settings.FolderId,
            settings.HasApiToken,
            settings.ApiTokenHint,
            settings.ApiTokenSetAtUtc,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? BuildHint(string value) => value.Length <= 4 ? null : value[^4..];

    private static string? GetConfigurationError(AudiobookshelfSettings settings)
    {
        if (GetRequirementError(settings) is { } requirementError)
        {
            return requirementError;
        }

        // LastTestSucceeded is reset to null by every settings/secret mutation
        // (AudiobookshelfSettings.ResetTestResult()), so this is specifically "a
        // successful test for the *currently saved* configuration," not a stale
        // pass from before the last edit.
        if (settings.LastTestSucceeded != true)
        {
            return "Test the connection and confirm it succeeds before enabling Audiobookshelf. " +
                   "Any settings change since the last test invalidates it.";
        }

        return null;
    }

    /// <summary>What must be saved before a connection test is worth running.</summary>
    private static string? GetRequirementError(AudiobookshelfSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return "A base URL is required before enabling Audiobookshelf.";
        }

        if (string.IsNullOrWhiteSpace(settings.LibraryId))
        {
            return "A library must be selected before enabling Audiobookshelf.";
        }

        if (!settings.HasApiToken)
        {
            return "An API token is required before enabling Audiobookshelf.";
        }

        return null;
    }
}

public sealed record AudiobookshelfConnectionTestResult(AudiobookshelfStatus Status, ConnectionTestOutcome Outcome);

public sealed record AudiobookshelfCommandResult(
    PublishingCommandOutcome Outcome, AudiobookshelfStatus? Status, string? Error)
{
    public static AudiobookshelfCommandResult Success(AudiobookshelfStatus status) =>
        new(PublishingCommandOutcome.Success, status, null);

    public static AudiobookshelfCommandResult Invalid(string error) =>
        new(PublishingCommandOutcome.Invalid, null, error);
}
