using FamilyLibrarian.Application.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Ties <c>/health/ready</c> to the exact same signal
/// <see cref="IAcquisitionBoundaryGuard"/> uses to fail closed, so an operator
/// watching readiness sees the same thing the acquisition boundary is enforcing.
/// </summary>
public sealed class SecurityScannerHealthCheck(IAcquisitionBoundaryGuard boundaryGuard) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canAccept = await boundaryGuard.CanAcceptNewArtifactAsync(cancellationToken);
        return canAccept
            ? HealthCheckResult.Healthy("All required malware scanners are reachable.")
            : HealthCheckResult.Unhealthy("A required malware scanner is unavailable.");
    }
}
