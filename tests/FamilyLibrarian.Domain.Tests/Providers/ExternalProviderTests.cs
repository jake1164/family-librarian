using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Domain.Tests.Providers;

[TestClass]
public sealed class ExternalProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void HealthIssuesAreReplacedByEveryProbeSoAStaleReasonNeverLingers()
    {
        var provider = new ExternalProvider("example-source", "Example", "https://example.test", Now);
        var issues = new[] { new ProviderHealthIssue("search", "no-indexers", "No indexer.") };

        provider.RecordTestResult(
            false, "x", "2", "operations:search", null, Now, healthStatus: "Degraded",
            searchOperationStatus: "Degraded", acquireOperationStatus: "Available",
            manifestReached: true, healthIssues: issues);
        CollectionAssert.AreEqual(issues, provider.CachedHealthIssues.ToArray());

        provider.RecordHealthCheck(true, "x", "Healthy", "Available", "Available", Now);
        Assert.AreEqual(0, provider.CachedHealthIssues.Count);

        provider.RecordHealthCheck(false, "x", "Degraded", "Degraded", "Available", Now, issues);
        CollectionAssert.AreEqual(issues, provider.CachedHealthIssues.ToArray());

        provider.RecordTestResult(
            true, "x", "2", "operations:search", null, Now, healthStatus: "Healthy",
            searchOperationStatus: "Available", acquireOperationStatus: "Available", manifestReached: true);
        Assert.AreEqual(0, provider.CachedHealthIssues.Count);
    }

    [TestMethod]
    public void ADegradedHealthResultStillUpdatesTheCachedHealthSearchAndAcquireStatus()
    {
        var provider = new ExternalProvider("libgen", "LibGen", "https://libgen.example", Now);
        provider.RecordTestResult(
            succeeded: true,
            message: "Reached LibGen (protocol v2).",
            protocolVersion: "2",
            capabilities: "operations:search,acquire",
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
            actorUserId: null,
            testedAtUtc: Now.AddMinutes(5));

        Assert.AreEqual(false, provider.LastTestSucceeded);
        Assert.AreEqual("Healthy", provider.CachedHealthStatus);
        Assert.AreEqual("Available", provider.CachedSearchOperationStatus);
        Assert.AreEqual("Available", provider.CachedAcquireOperationStatus);
    }

    [TestMethod]
    public void ANewProviderStartsWithTheDefaultAutomaticAttemptLimit()
    {
        var provider = new ExternalProvider("example-source", "Example Source", "https://source.invalid", Now);

        Assert.AreEqual(ExternalProvider.DefaultAutomaticAttemptLimit, provider.AutomaticAttemptLimit);
        Assert.AreEqual(3, provider.AutomaticAttemptLimit);
    }

    [TestMethod]
    public void AMeteredSourceCanBeLimitedToASingleAutomaticAttempt()
    {
        // The setting an administrator uses for a source with a limited
        // download allowance: spend one download, then ask a person.
        var provider = new ExternalProvider("example-source", "Example Source", "https://source.invalid", Now);

        provider.SetAutomaticAttemptLimit(1, actorUserId: null, Now);

        Assert.AreEqual(1, provider.AutomaticAttemptLimit);
    }

    [TestMethod]
    public void AnAutomaticAttemptLimitOutsideTheAllowedRangeIsRejected()
    {
        var provider = new ExternalProvider("example-source", "Example Source", "https://source.invalid", Now);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => provider.SetAutomaticAttemptLimit(0, actorUserId: null, Now));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => provider.SetAutomaticAttemptLimit(
                ExternalProvider.MaximumAutomaticAttemptLimit + 1, actorUserId: null, Now));
    }
}
