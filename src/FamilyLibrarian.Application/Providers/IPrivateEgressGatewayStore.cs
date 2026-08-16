using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

public interface IPrivateEgressGatewayStore
{
    Task<PrivateEgressGatewaySettings?> FindAsync(CancellationToken cancellationToken);

    Task<PrivateEgressGatewaySettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
