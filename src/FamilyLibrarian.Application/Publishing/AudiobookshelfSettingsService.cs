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

    public async Task<AudiobookshelfCommandResult> SetEnabledAsync(
        bool isEnabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
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
        string? baseUrl, string? libraryId, string? folderId, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetSettings(baseUrl, libraryId, folderId, currentUser.UserId, clock.UtcNow);
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

    public async Task<AudiobookshelfCommandResult> TestConnectionAsync(
        CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        var outcome = await connectionTester.TestAsync(settings, cancellationToken);

        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.PublishingDestinationTested,
            AuditSubjectTypes.PublishingDestination,
            "audiobookshelf",
            new { Destination = "audiobookshelf", outcome.Succeeded },
            cancellationToken);

        return AudiobookshelfCommandResult.Success(ToStatus(settings));
    }

    private static AudiobookshelfStatus ToStatus(AudiobookshelfSettings? settings) => settings is null
        ? new AudiobookshelfStatus(false, null, null, null, false, null, null, null, null, null)
        : new AudiobookshelfStatus(
            settings.IsEnabled,
            settings.BaseUrl,
            settings.LibraryId,
            settings.FolderId,
            settings.HasApiToken,
            settings.ApiTokenHint,
            settings.ApiTokenSetAtUtc,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);

    private static string? BuildHint(string value) => value.Length <= 4 ? null : value[^4..];
}

public sealed record AudiobookshelfCommandResult(
    PublishingCommandOutcome Outcome, AudiobookshelfStatus? Status, string? Error)
{
    public static AudiobookshelfCommandResult Success(AudiobookshelfStatus status) =>
        new(PublishingCommandOutcome.Success, status, null);

    public static AudiobookshelfCommandResult Invalid(string error) =>
        new(PublishingCommandOutcome.Invalid, null, error);
}
