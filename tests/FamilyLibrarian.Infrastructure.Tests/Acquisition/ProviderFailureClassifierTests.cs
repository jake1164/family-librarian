using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ProviderFailureClassifierTests
{
    [TestMethod]
    [DataRow("AUTH_REQUIRED", ProviderFailureKind.SourceProblem)]
    [DataRow("AUTH_FAILED", ProviderFailureKind.SourceProblem)]
    [DataRow("VPN_UNAVAILABLE", ProviderFailureKind.SourceProblem)]
    [DataRow("RATE_LIMITED", ProviderFailureKind.Throttled)]
    [DataRow("DOWNLOAD_FAILED", ProviderFailureKind.CopyProblem)]
    [DataRow("HASH_MISMATCH", ProviderFailureKind.CopyProblem)]
    [DataRow("CONTENT_UNAVAILABLE", ProviderFailureKind.CopyProblem)]
    [DataRow("UPSTREAM_UNAVAILABLE", ProviderFailureKind.CopyProblem)]
    [DataRow("CANDIDATE_CHANGED", ProviderFailureKind.CopyProblem)]
    [DataRow("PROVIDER_TIMEOUT", ProviderFailureKind.CopyProblem)]
    [DataRow("SOMETHING_NEW", ProviderFailureKind.CopyProblem)]
    [DataRow("auth_failed", ProviderFailureKind.CopyProblem)]
    [DataRow(null, ProviderFailureKind.CopyProblem)]
    [DataRow("", ProviderFailureKind.CopyProblem)]
    public void ACodeIsClassifiedByWhatItSaysAboutTheCopy(string? code, ProviderFailureKind expected) =>
        Assert.AreEqual(expected, ProviderFailureClassifier.Classify(code));

    [TestMethod]
    [DataRow(null, 900)]
    [DataRow(0, 900)]
    [DataRow(-5, 900)]
    [DataRow(1, 300)]
    [DataRow(1800, 1800)]
    [DataRow(10_000_000, 86_400)]
    public void ThePausePrefersWhatTheSourceAskedForWithinBounds(int? retryAfterSeconds, int expectedSeconds) =>
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), ProviderFailureClassifier.ThrottleDelay(retryAfterSeconds));
}
