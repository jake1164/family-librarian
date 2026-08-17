using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Probes CWA reachability without touching the real ingest workflow — never
/// routes a probe through <see cref="ICwaIngestTransport"/>, which targets the
/// actual watched folder with a real book filename.
/// </summary>
public interface ICwaConnectionTester
{
    Task<ConnectionTestOutcome> TestAsync(
        CwaSettings settings,
        CwaConnectionTestTarget target,
        CancellationToken cancellationToken);
}

public enum CwaConnectionTestTarget
{
    Ingest,
    Opds,
    All
}
