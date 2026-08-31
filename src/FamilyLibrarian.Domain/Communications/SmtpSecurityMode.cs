namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// The encrypted transport modes supported for the outbound SMTP provider.
/// Plaintext SMTP is intentionally not an option.
/// </summary>
public enum SmtpSecurityMode
{
    StartTls,
    SslOnConnect
}
