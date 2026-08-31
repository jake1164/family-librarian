using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe SMTP test double; ordinary web tests never contact an SMTP server.</summary>
internal sealed class AlwaysSucceedsSmtpTestSender : ISmtpTestSender
{
    public Task<ConnectionTestOutcome> SendTestAsync(
        SmtpSettings settings, string recipientAddress, CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectionTestOutcome(true, "Test double: SMTP accepted the test email."));
}
