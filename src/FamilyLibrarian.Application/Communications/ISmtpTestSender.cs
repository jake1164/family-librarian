using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>Sends the administrator-requested SMTP probe for a saved provider configuration.</summary>
public interface ISmtpTestSender
{
    Task<ConnectionTestOutcome> SendTestAsync(
        SmtpSettings settings, string recipientAddress, CancellationToken cancellationToken);
}
