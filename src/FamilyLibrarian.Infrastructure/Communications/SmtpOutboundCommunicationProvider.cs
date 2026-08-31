using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Communications;
using MimeKit;

namespace FamilyLibrarian.Infrastructure.Communications;

/// <summary>
/// The real (non-test) SMTP send path: translates a normalized
/// <see cref="OutboundCommunication"/> into an email and sends it through the
/// administrator-configured SMTP settings, over the same
/// <see cref="SmtpMailTransport"/> the "send test email" probe uses.
/// </summary>
public sealed class SmtpOutboundCommunicationProvider(
    ISmtpSettingsStore settingsStore,
    IUserEmailLookup emailLookup,
    ICredentialProtector protector) : IOutboundCommunicationProvider
{
    public string ProviderId => "smtp";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        return settings?.IsEnabled == true;
    }

    public async Task<SendResult> SendAsync(OutboundCommunication communication, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true, Host: not null, Port: not null, FromAddress: not null })
        {
            return SendResult.Failure("SMTP is not fully configured.");
        }

        var recipientAddress = await emailLookup.GetEmailAsync(communication.RecipientUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipientAddress))
        {
            return SendResult.Failure("The recipient has no email address on file.");
        }

        var password = settings.HasPassword
            ? protector.Unprotect(
                CommunicationSecretPurposes.SmtpPassword, settings.ProtectedPassword!, settings.PasswordFormatVersion)
            : null;
        if (settings.HasPassword && password is null)
        {
            return SendResult.Failure("The stored SMTP password could not be decrypted.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipientAddress));
        message.Subject = communication.Subject ?? "Family Librarian notification";
        message.Body = new TextPart("plain") { Text = BuildBody(communication) };

        try
        {
            await SmtpMailTransport.SendAsync(settings, password, message, cancellationToken);
            return SendResult.Success();
        }
        catch (SmtpTransportException exception)
        {
            return SendResult.Failure(exception.Message);
        }
    }

    private static string BuildBody(OutboundCommunication communication) =>
        communication.Link is null ? communication.Body : $"{communication.Body}{Environment.NewLine}{Environment.NewLine}{communication.Link}";
}
