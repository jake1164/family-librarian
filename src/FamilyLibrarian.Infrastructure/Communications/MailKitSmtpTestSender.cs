using System.Net.Sockets;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FamilyLibrarian.Infrastructure.Communications;

/// <summary>
/// MailKit-backed SMTP test sender. This is purposely limited to the explicit
/// administrator probe; operational notification delivery will use the same
/// transport behind a durable delivery outbox in a later slice.
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
            using var client = new SmtpClient();
            client.Timeout = (int)TimeSpan.FromSeconds(15).TotalMilliseconds;
            await client.ConnectAsync(
                settings.Host!, settings.Port!.Value, ToSecureSocketOptions(settings.SecurityMode), cancellationToken);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, password!, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return new ConnectionTestOutcome(true, "SMTP accepted the test email for delivery.");
        }
        catch (AuthenticationException)
        {
            return new ConnectionTestOutcome(false, "SMTP authentication was rejected.");
        }
        catch (SslHandshakeException)
        {
            return new ConnectionTestOutcome(false, "SMTP TLS negotiation failed. Check the selected security mode and server certificate.");
        }
        catch (SmtpCommandException)
        {
            return new ConnectionTestOutcome(false, "The SMTP server rejected the test email.");
        }
        catch (SmtpProtocolException)
        {
            return new ConnectionTestOutcome(false, "The SMTP server returned an invalid protocol response.");
        }
        catch (SocketException)
        {
            return new ConnectionTestOutcome(false, "The SMTP server could not be reached.");
        }
        catch (IOException)
        {
            return new ConnectionTestOutcome(false, "The SMTP connection was interrupted.");
        }
    }

    private static SecureSocketOptions ToSecureSocketOptions(SmtpSecurityMode mode) => mode switch
    {
        SmtpSecurityMode.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported SMTP security mode.")
    };
}
