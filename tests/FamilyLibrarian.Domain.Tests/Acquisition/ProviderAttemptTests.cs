using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

[TestClass]
public sealed class ProviderAttemptTests
{
    [TestMethod]
    public void AConfiguredGutenbergTrackLimitIsAConfigurationIssue()
    {
        var attempt = new ProviderAttempt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "gutendex",
            ProviderAttemptOutcome.Failed,
            "The Gutenberg audiobook exceeds the configured track limit.",
            DateTimeOffset.UtcNow,
            nextEligibleCheckAtUtc: null);

        Assert.AreEqual(ProviderAttemptIssueKind.Configuration, attempt.IssueKind);
    }

    [TestMethod]
    public void AProviderTransportFailureIsAnOperationalIssue()
    {
        var attempt = new ProviderAttempt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "gutendex",
            ProviderAttemptOutcome.Failed,
            "The automatic provider could not be reached; it will be tried again automatically.",
            DateTimeOffset.UtcNow,
            nextEligibleCheckAtUtc: null);

        Assert.AreEqual(ProviderAttemptIssueKind.Operational, attempt.IssueKind);
    }
}
