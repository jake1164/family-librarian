using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ProviderSourceHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void OneFailedLookupDoesNotFlagTheSource()
    {
        var issues = ProviderSourceHealth.CurrentIssues([Attempt("librivox", ProviderAttemptOutcome.Failed, 1)], Now);

        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void ARepeatedFailureFlagsTheSource()
    {
        var issues = ProviderSourceHealth.CurrentIssues(
            [
                Attempt("librivox", ProviderAttemptOutcome.Failed, 1),
                Attempt("librivox", ProviderAttemptOutcome.Failed, 2),
                Attempt("librivox", ProviderAttemptOutcome.Failed, 3)
            ],
            Now);

        Assert.AreEqual(1, issues.Count);
        Assert.AreEqual("librivox", issues[0].ProviderId);
    }

    [TestMethod]
    public void ASuccessBetweenFailuresBreaksTheRun()
    {
        var issues = ProviderSourceHealth.CurrentIssues(
            [
                Attempt("librivox", ProviderAttemptOutcome.Failed, 1),
                Attempt("librivox", ProviderAttemptOutcome.NoMatch, 2),
                Attempt("librivox", ProviderAttemptOutcome.Failed, 3),
                Attempt("librivox", ProviderAttemptOutcome.Failed, 4)
            ],
            Now);

        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void AnOldFailureStopsBeingReportedWithoutAnyoneClearingIt()
    {
        var issues = ProviderSourceHealth.CurrentIssues(
            [
                Attempt("librivox", ProviderAttemptOutcome.Failed, 26 * 60),
                Attempt("librivox", ProviderAttemptOutcome.Failed, 27 * 60),
                Attempt("librivox", ProviderAttemptOutcome.Failed, 28 * 60)
            ],
            Now);

        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void ABlockedLookupIsStillReportedBecauseItNeedsAConfigurationChange()
    {
        var issues = ProviderSourceHealth.CurrentIssues([Attempt("gutenberg", ProviderAttemptOutcome.Blocked, 1)], Now);

        Assert.AreEqual(1, issues.Count);
    }

    [TestMethod]
    public void ProvidersAreJudgedIndependently()
    {
        var issues = ProviderSourceHealth.CurrentIssues(
            [
                Attempt("a", ProviderAttemptOutcome.Failed, 1),
                Attempt("b", ProviderAttemptOutcome.Failed, 2),
                Attempt("a", ProviderAttemptOutcome.Failed, 3),
                Attempt("b", ProviderAttemptOutcome.NoMatch, 4),
                Attempt("a", ProviderAttemptOutcome.Failed, 5)
            ],
            Now);

        Assert.AreEqual(1, issues.Count);
        Assert.AreEqual("a", issues[0].ProviderId);
    }

    private static ProviderAttempt Attempt(string providerId, ProviderAttemptOutcome outcome, int minutesAgo) =>
        new(Guid.NewGuid(), Guid.NewGuid(), providerId, outcome, "summary", Now.AddMinutes(-minutesAgo), null);
}
