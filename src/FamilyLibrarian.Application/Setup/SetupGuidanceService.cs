using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Setup;

namespace FamilyLibrarian.Application.Setup;

/// <summary>Administrator commands for what family members are told while setting up delivery and messaging.</summary>
public sealed class SetupGuidanceService(
    ISetupGuidanceStore store,
    IMatrixSettingsStore matrixSettingsStore,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const int MaxSaveAttempts = 5;

    public async Task<SetupGuidanceStatus> GetAsync(CancellationToken cancellationToken) =>
        await ToStatusAsync(await store.FindAsync(cancellationToken), cancellationToken);

    public Task<SetupGuidanceResult> SetKindleAsync(
        string? senderAddress, string? amazonStore, CancellationToken cancellationToken) =>
        ChangeAsync(
            settings => settings.TrySetKindle(senderAddress, amazonStore, currentUser.UserId, clock.UtcNow),
            "kindle", cancellationToken);

    public Task<SetupGuidanceResult> SetMessagingAsync(
        string? serverName,
        string? signInInstructions,
        string? helpUrl,
        string? webAppUrl,
        CancellationToken cancellationToken) =>
        ChangeAsync(
            settings => settings.TrySetMessaging(
                serverName, signInInstructions, helpUrl, webAppUrl, currentUser.UserId, clock.UtcNow),
            "messaging", cancellationToken);

    public Task<SetupGuidanceResult> SetShowElementHelpAsync(bool show, CancellationToken cancellationToken) =>
        ChangeAsync(
            settings =>
            {
                settings.SetShowElementHelp(show, currentUser.UserId, clock.UtcNow);
                return null;
            },
            "messaging", cancellationToken);

    /// <summary>
    /// Each change writes only its own card's values, so when another save wins the race it is read again and the
    /// change applied on top: neither save is lost.
    /// </summary>
    private async Task<SetupGuidanceResult> ChangeAsync(
        Func<SetupGuidanceSettings, string?> change, string section, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
        {
            var settings = await store.GetOrCreateAsync(cancellationToken);
            if (change(settings) is { } error)
            {
                return SetupGuidanceResult.Invalid(error);
            }

            if (await store.TrySaveChangesAsync(cancellationToken))
            {
                await audit.WriteAsync(
                    AuditActions.SetupGuidanceChanged, AuditSubjectTypes.SetupGuidance, section,
                    new { Section = section }, cancellationToken);
                return SetupGuidanceResult.Success(await ToStatusAsync(settings, cancellationToken));
            }
        }

        return SetupGuidanceResult.Conflict();
    }

    private async Task<SetupGuidanceStatus> ToStatusAsync(
        SetupGuidanceSettings? settings, CancellationToken cancellationToken) => new(
        settings?.KindleSenderAddress,
        settings?.AmazonStoreDomain,
        settings?.AmazonStoreDomain ?? AmazonStore.DefaultDomain,
        AmazonStore.FindAddressUrl(settings?.AmazonStoreDomain),
        AmazonStore.ManageContentUrl(settings?.AmazonStoreDomain),
        settings?.MessagingServerName,
        MatrixUserIds.ServerNameOf((await matrixSettingsStore.FindAsync(cancellationToken))?.BotUserId),
        settings?.MessagingSignInInstructions,
        settings?.MessagingHelpUrl,
        settings?.MessagingWebAppUrl,
        settings?.ShowElementHelp ?? true);
}

/// <param name="AmazonStoreDomain">The saved store; <see langword="null"/> while the default applies.</param>
/// <param name="SuggestedServerName">Used while <paramref name="MessagingServerName"/> is blank.</param>
public sealed record SetupGuidanceStatus(
    string? KindleSenderAddress,
    string? AmazonStoreDomain,
    string EffectiveAmazonStoreDomain,
    string FindKindleAddressUrl,
    string ManageContentUrl,
    string? MessagingServerName,
    string? SuggestedServerName,
    string? MessagingSignInInstructions,
    string? MessagingHelpUrl,
    string? MessagingWebAppUrl,
    bool ShowElementHelp);

public sealed record SetupGuidanceResult(
    bool Succeeded, SetupGuidanceStatus? Status, string? Error, bool IsConflict = false)
{
    public static SetupGuidanceResult Success(SetupGuidanceStatus status) => new(true, status, null);

    public static SetupGuidanceResult Invalid(string error) => new(false, null, error);

    public static SetupGuidanceResult Conflict() =>
        new(false, null, "These settings are being changed somewhere else. Reload the page and try again.", true);
}
