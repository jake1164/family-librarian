namespace FamilyLibrarian.Application.Security;

/// <summary>
/// The single check that keeps required-scanner unavailability from blocking
/// only delivery: nothing may accept a new file — upload, provider download, or
/// linked-library staging — while this reports false.
/// </summary>
public interface IAcquisitionBoundaryGuard
{
    Task<bool> CanAcceptNewArtifactAsync(CancellationToken cancellationToken);
}

public sealed class AcquisitionBoundaryGuard(IEnumerable<IMalwareScanner> scanners) : IAcquisitionBoundaryGuard
{
    public async Task<bool> CanAcceptNewArtifactAsync(CancellationToken cancellationToken)
    {
        foreach (var scanner in scanners.Where(scanner => scanner.IsRequired))
        {
            var health = await scanner.CheckHealthAsync(cancellationToken);
            if (!health.IsHealthy)
            {
                return false;
            }
        }

        return true;
    }
}
