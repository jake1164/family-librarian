using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

/// <summary>
/// A step the retry loop is already handling must not look like, or be counted
/// as, a problem for an administrator. The notification tray lists exactly the
/// attempts whose <see cref="ProviderAttempt.IssueKind"/> is not null.
/// </summary>
[TestClass]
public sealed class ProviderAttemptRetryingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static ProviderAttempt Attempt(ProviderAttemptOutcome outcome, string summary = "Something happened.") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "prowlarr", outcome, summary, Now, nextEligibleCheckAtUtc: Now);

    [TestMethod]
    public void AStepThatMovesOnToTheNextCopyIsNotAnAdministratorIssue()
    {
        Assert.IsNull(Attempt(ProviderAttemptOutcome.Retrying).IssueKind);
    }

    [TestMethod]
    public void AnInProgressSubmissionIsNotAnAdministratorIssue()
    {
        Assert.IsNull(Attempt(ProviderAttemptOutcome.Submitted).IssueKind);
    }

    [TestMethod]
    public void ARealFailureIsStillAnOperationalIssue()
    {
        Assert.AreEqual(ProviderAttemptIssueKind.Operational, Attempt(ProviderAttemptOutcome.Failed).IssueKind);
    }

    [TestMethod]
    public void ABlockedLookupIsStillAConfigurationIssue()
    {
        Assert.AreEqual(ProviderAttemptIssueKind.Configuration, Attempt(ProviderAttemptOutcome.Blocked).IssueKind);
    }
}
