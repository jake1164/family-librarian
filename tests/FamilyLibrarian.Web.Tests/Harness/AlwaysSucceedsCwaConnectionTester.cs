using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe CWA connection-test fake: no ordinary test depends on a reachable CWA instance.</summary>
internal sealed class AlwaysSucceedsCwaConnectionTester : ICwaConnectionTester
{
    public Task<ConnectionTestOutcome> TestAsync(
        CwaSettings settings, CwaConnectionTestTarget target, CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectionTestOutcome(true, "Test double: always succeeds."));
}
