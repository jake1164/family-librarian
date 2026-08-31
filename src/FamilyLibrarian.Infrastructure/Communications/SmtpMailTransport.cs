using System.Net.Sockets;
using FamilyLibrarian.Domain.Communications;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FamilyLibrarian.Infrastructure.Communications;

/// <summary>
/// Thrown by <see cref="SmtpMailTransport"/> with a message already safe to
/// show an administrator or record as a delivery error.
/// </summary>
internal sealed class SmtpTransportException(string message) : Exception(message);

/// <summary>
/// The single MailKit connect/authenticate/send path shared by the admin
/// "send test email" probe (<see cref="MailKitSmtpTestSender"/>) and the real
/// outbound provider (<see cref="SmtpOutboundCommunicationProvider"/>), so
/// there is exactly one place that talks to an SMTP server.
/// </summary>
internal static class SmtpMailTransport
{
    public static async Task SendAsync(
        SmtpSettings settings, string? password, MimeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new SmtpClient();
            client.Timeout = (int)TimeSpan.FromSeconds(15).TotalMilliseconds;
            // MailKit performs full online revocation (CRL/OCSP) checking by
            // default, unlike most SMTP clients. That fails a household's own
            // mail relay using an internally-issued/self-signed certificate,
            // or any deployment with restricted outbound egress that can't
            // reach a revocation responder, even though the certificate is
            // otherwise valid and trusted. Chain/trust/expiry/hostname
            // validation still applies; only revocation freshness is relaxed.
            client.CheckCertificateRevocation = false;
            await client.ConnectAsync(
                settings.Host!, settings.Port!.Value, ToSecureSocketOptions(settings.SecurityMode), cancellationToken);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, password!, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (AuthenticationException)
        {
            throw new SmtpTransportException("SMTP authentication was rejected.");
        }
        catch (SslHandshakeException)
        {
            throw new SmtpTransportException(
                "SMTP TLS negotiation failed. Check the selected security mode and server certificate.");
        }
        catch (SmtpCommandException)
        {
            throw new SmtpTransportException("The SMTP server rejected the message.");
        }
        catch (SmtpProtocolException)
        {
            throw new SmtpTransportException("The SMTP server returned an invalid protocol response.");
        }
        catch (SocketException)
        {
            throw new SmtpTransportException("The SMTP server could not be reached.");
        }
        catch (IOException)
        {
            throw new SmtpTransportException("The SMTP connection was interrupted.");
        }
    }

    public static SecureSocketOptions ToSecureSocketOptions(SmtpSecurityMode mode) => mode switch
    {
        SmtpSecurityMode.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported SMTP security mode.")
    };
}
