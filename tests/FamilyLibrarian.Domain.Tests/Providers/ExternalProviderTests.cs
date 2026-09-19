using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Domain.Tests.Providers;

[TestClass]
public sealed class ExternalProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ADegradedHealthResultStillUpdatesTheCachedHealthSearchAndAcquireStatus()
    {
        var provider = new ExternalProvider("libgen", "LibGen", "https://libgen.example", Now);
        provider.RecordTestResult(
            succeeded: true,
            message: "Reached LibGen (protocol v2).",
            protocolVersion: "2",
            capabilities: "operations:search,acquire",
            egressPolicy: EgressPolicy.Normal,
            actorUserId: null,
            testedAtUtc: Now,
            instanceId: "instance-1",
            healthStatus: "Healthy",
            searchOperationStatus: "Available",
            acquireOperationStatus: "Available",
            manifestReached: true);

        // A later test reaches the manifest and /health again, but this time
        // /health itself reports degraded (search unavailable) -- not fully
        // operational, yet still a fresh, real observation of the provider's
        // reported health/search/acquire state.
        provider.RecordTestResult(
            succeeded: false,
            message: "The manifest was reachable, but LibGen reported its search or acquire capability as unavailable.",
            protocolVersion: "2",
            capabilities: "operations:search,acquire",
            egressPolicy: EgressPolicy.Normal,
            actorUserId: null,
            testedAtUtc: Now.AddMinutes(5),
            instanceId: "instance-1",
            healthStatus: "Degraded",
            searchOperationStatus: "Unavailable",
            acquireOperationStatus: "Available",
            manifestReached: true);

        Assert.AreEqual(false, provider.LastTestSucceeded);
        Assert.AreEqual("Degraded", provider.CachedHealthStatus);
        Assert.AreEqual("Unavailable", provider.CachedSearchOperationStatus);
        Assert.AreEqual("Available", provider.CachedAcquireOperationStatus);
    }

    [TestMethod]
    public void AnUnreachableProviderPreservesThePreviouslyCachedHealthStatus()
    {
        var provider = new ExternalProvider("libgen", "LibGen", "https://libgen.example", Now);
        provider.RecordTestResult(
            succeeded: true,
            message: "Reached LibGen (protocol v2).",
            protocolVersion: "2",
            capabilities: "operations:search,acquire",
            egressPolicy: EgressPolicy.Normal,
            actorUserId: null,
            testedAtUtc: Now,
            instanceId: "instance-1",
            healthStatus: "Healthy",
            searchOperationStatus: "Available",
            acquireOperationStatus: "Available",
            manifestReached: true);

        // No manifest/health response at all this time -- the cached values
        // from the last real probe must survive untouched.
        provider.RecordTestResult(
            succeeded: false,
            message: "The provider is unreachable: timed out.",
            protocolVersion: provider.CachedProtocolVersion,
            capabilities: provider.CachedCapabilities,
            egressPolicy: provider.CachedEgressPolicy,
            actorUserId: null,
            testedAtUtc: Now.AddMinutes(5));

        Assert.AreEqual(false, provider.LastTestSucceeded);
        Assert.AreEqual("Healthy", provider.CachedHealthStatus);
        Assert.AreEqual("Available", provider.CachedSearchOperationStatus);
        Assert.AreEqual("Available", provider.CachedAcquireOperationStatus);
    }
}
