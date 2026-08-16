using FamilyLibrarian.Application.Accounts;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe discovery-tester fake: no test ever depends on a reachable identity provider.</summary>
internal sealed class AlwaysSucceedsOidcDiscoveryTester : IOidcDiscoveryTester
{
    public Task<OidcDiscoveryTestOutcome> TestAsync(string? authority, CancellationToken cancellationToken) =>
        Task.FromResult(OidcDiscoveryTestOutcome.Success(new OidcDiscoveryEndpoints(
            "https://issuer.test/authorize",
            "https://issuer.test/token",
            "https://issuer.test/userinfo",
            "https://issuer.test/jwks",
            "https://issuer.test/logout")));
}
