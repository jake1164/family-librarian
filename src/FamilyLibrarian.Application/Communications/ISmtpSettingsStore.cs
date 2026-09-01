using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

public interface ISmtpSettingsStore
{
    Task<SmtpSettings?> FindAsync(CancellationToken cancellationToken);

    Task<SmtpSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
