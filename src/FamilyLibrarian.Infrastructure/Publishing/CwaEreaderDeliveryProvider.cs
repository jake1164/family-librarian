using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// The CWA implementation of <see cref="IEbookDeliveryProvider"/> --
/// resolves the saved e-reader service account, delegates the actual
/// login/send mechanics to <see cref="ICwaEreaderSessionClient"/>, and maps
/// its CWA-flavored result onto the provider-boundary-neutral
/// <see cref="EbookDeliveryOutcome"/>/<see cref="ConnectionTestOutcome"/> shapes.
/// </summary>
public sealed class CwaEreaderDeliveryProvider(
    ICwaSettingsStore settingsStore,
    ICredentialProtector protector,
    ICwaEreaderSessionClient sessionClient) : IEbookDeliveryProvider
{
    public string Id => "cwa";

    public async Task<bool> CanDeliverAsync(CancellationToken cancellationToken) =>
        IsConfigured(await settingsStore.FindAsync(cancellationToken));

    public async Task<EbookDeliveryOutcome> DeliverAsync(
        string providerBookId, string bookFormat, bool convert, string recipientEmail,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (!IsConfigured(settings))
        {
            return EbookDeliveryOutcome.NotConfigured(
                "The e-reader delivery service account is not configured. Ask an administrator to set it up.");
        }

        var password = ResolvePassword(settings!);
        if (password is null)
        {
            return EbookDeliveryOutcome.NotConfigured(
                "The saved e-reader delivery service account password can no longer be decrypted. " +
                "Ask an administrator to re-enter it.");
        }

        var result = await sessionClient.SendSelectedAsync(
            settings!, settings!.EreaderServiceAccountUsername!, password,
            providerBookId, bookFormat, convert, recipientEmail, cancellationToken);

        return result.Status switch
        {
            CwaEreaderSendStatus.Success => EbookDeliveryOutcome.Delivered("The book was sent successfully."),
            CwaEreaderSendStatus.LoginFailed => EbookDeliveryOutcome.NotConfigured(
                "The e-reader delivery service account could not sign in. " +
                "Ask an administrator to check its saved username and password."),
            CwaEreaderSendStatus.SendRejected => EbookDeliveryOutcome.Rejected(
                result.CwaMessage ?? "The library declined to send this book."),
            _ => EbookDeliveryOutcome.TransportFailure(
                "The e-reader delivery destination could not be reached. Try again later."),
        };
    }

    public async Task<ConnectionTestOutcome> TestAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (!IsConfigured(settings))
        {
            return new ConnectionTestOutcome(
                false, "Configure an e-reader delivery service account username and password before testing.");
        }

        var password = ResolvePassword(settings!);
        if (password is null)
        {
            return new ConnectionTestOutcome(
                false, "The saved e-reader delivery service account password can no longer be decrypted. " +
                       "Enter it again, then save and test.");
        }

        var login = await sessionClient.TestLoginAsync(
            settings!, settings!.EreaderServiceAccountUsername!, password, cancellationToken);
        return new ConnectionTestOutcome(login.Succeeded, login.Message);
    }

    private string? ResolvePassword(CwaSettings settings) =>
        settings.HasEreaderServiceAccountPassword
            ? protector.Unprotect(
                PublishingSecretPurposes.CwaEreaderServiceAccountPassword,
                settings.ProtectedEreaderServiceAccountPassword!,
                settings.EreaderServiceAccountPasswordFormatVersion)
            : null;

    private static bool IsConfigured(CwaSettings? settings) =>
        settings is not null &&
        !string.IsNullOrWhiteSpace(settings.EreaderServiceAccountUsername) &&
        settings.HasEreaderServiceAccountPassword &&
        !string.IsNullOrWhiteSpace(settings.OpdsBaseUrl);
}
