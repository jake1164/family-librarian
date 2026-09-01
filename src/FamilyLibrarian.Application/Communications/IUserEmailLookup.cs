namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Resolves a user's account email address for the SMTP provider. Kept
/// separate from <see cref="Domain.Communications.OutboundCommunication"/> so
/// the normalized message never carries a provider-specific destination.
/// </summary>
public interface IUserEmailLookup
{
    Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken);
}
