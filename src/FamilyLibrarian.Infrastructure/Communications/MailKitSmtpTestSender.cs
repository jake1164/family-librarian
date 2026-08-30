using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;
using MimeKit;

namespace FamilyLibrarian.Infrastructure.Communications;

/// <summary>
/// MailKit-backed SMTP test sender: the explicit administrator probe used to
/// validate settings before they can be enabled. Real notification delivery
/// goes through <see cref="SmtpOutboundCommunicationProvider"/> instead, over
/// the same <see cref="SmtpMailTransport"/>.
/// </summary>
public sealed class MailKitSmtpTestSender(ICredentialProtector protector) : ISmtpTestSender
{
    public async Task<ConnectionTestOutcome> SendTestAsync(
        SmtpSettings settings, string recipientAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var password = settings.HasPassword
            ? protector.Unprotect(
                CommunicationSecretPurposes.SmtpPassword,
                settings.ProtectedPassword!,
                settings.PasswordFormatVersion)
            : null;
        if (settings.HasPassword && password is null)
        {
            return new ConnectionTestOutcome(false, "The stored SMTP password could not be decrypted.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress!));
        message.To.Add(MailboxAddress.Parse(recipientAddress));
        message.Subject = "Family Librarian SMTP test";
        message.Body = new TextPart("plain")
        {
            Text = "This is a test email from Family Librarian. SMTP configuration is working."
        };

        try
        {
            await SmtpMailTransport.SendAsync(settings, password, message, cancellationToken);
            return new ConnectionTestOutcome(true, "SMTP accepted the test email for delivery.");
        }
        catch (SmtpTransportException exception)
        {
            return new ConnectionTestOutcome(false, exception.Message);
        }
    }
}
