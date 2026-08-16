using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Application.Accounts;

public interface IOidcSettingsStore
{
    Task<OidcSettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Returns the existing row, or a new unsaved one when OIDC has never been configured.</summary>
    Task<OidcSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
