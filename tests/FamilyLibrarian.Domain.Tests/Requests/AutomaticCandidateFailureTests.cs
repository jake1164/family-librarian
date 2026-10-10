using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Requests;

/// <summary>
/// The attempt budget behind the acquisition retry loop (PROVIDER-7). The
/// distinction these tests pin down is the one that matters: a requester
/// setting an edition aside is free, while a download that failed verification
/// has spent a real transfer.
/// </summary>
[TestClass]
public sealed class AutomaticCandidateFailureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static BookRequest NewRequest(out Guid formatId)
    {
        var request = new BookRequest(Guid.NewGuid(), Guid.NewGuid(), [RequestMediaType.Ebook], null, Now);
        formatId = request.Formats.Single().Id;
        return request;
    }

    [TestMethod]
    public void AFailedAutomaticCandidateIsCounted()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Identity mismatch.", Now);

        Assert.AreEqual(1, request.CountAutomaticCandidateFailures(formatId, "example-indexer"));
    }

    [TestMethod]
    public void RecordingTheSameCandidateTwiceDoesNotDoubleCountTheBudget()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Identity mismatch.", Now);
        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Identity mismatch.", Now);

        Assert.AreEqual(1, request.CountAutomaticCandidateFailures(formatId, "example-indexer"));
    }

    [TestMethod]
    public void DistinctFailedCandidatesEachSpendOneAttempt()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Malware detected.", Now);
        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_two", "Identity mismatch.", Now);
        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_three", "Malformed EPUB.", Now);

        Assert.AreEqual(3, request.CountAutomaticCandidateFailures(formatId, "example-indexer"));
    }

    [TestMethod]
    public void AnotherProvidersFailuresDoNotSpendThisProvidersBudget()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "other-source", "c_one", "Identity mismatch.", Now);

        Assert.AreEqual(0, request.CountAutomaticCandidateFailures(formatId, "example-indexer"));
    }

    [TestMethod]
    public void TheFailureReasonIsRetainedForTheEventualReview()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Not the requested book.", Now);

        var declined = request.DeclinedCandidates.Single();
        Assert.AreEqual(DeclinedCandidateReason.AutomaticVerificationFailed, declined.Reason);
        Assert.AreEqual("Not the requested book.", declined.FailureReason);
    }

    [TestMethod]
    public void RecordingAFailureDoesNotByItselfMoveTheRequestToReview()
    {
        // One bad copy is a reason to try the next candidate, not to stop.
        // Whether the budget is spent is the caller's decision.
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Identity mismatch.", Now);

        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
    }

    [TestMethod]
    public void ARequesterKeepLookingDeclineDoesNotSpendTheAutomaticBudget()
    {
        // Otherwise someone browsing editions could silently exhaust
        // unattended acquisition for their own request.
        var request = NewRequest(out var formatId);
        request.MarkNeedsReview(
            RequestReviewCategory.PreferenceAmbiguity, "Two editions.", Now,
            [new RequestReviewCandidateInput(formatId, "example-indexer", "c_one", "Fahrenheit 451", "Ray Bradbury", "en", null, null, null, false)]);

        request.DismissReviewPreference(null, Now);

        Assert.AreEqual(1, request.DeclinedCandidates.Count);
        Assert.AreEqual(DeclinedCandidateReason.RequesterDeclined, request.DeclinedCandidates.Single().Reason);
        Assert.AreEqual(0, request.CountAutomaticCandidateFailures(formatId, "example-indexer"));
    }

    [TestMethod]
    public void TheReleaseFingerprintIsKeptWithTheFailure()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(
            formatId, "example-indexer", "c_one", "Not the requested book.", Now, releaseFingerprint: "ABC123");

        Assert.AreEqual("ABC123", request.DeclinedCandidates.Single().ReleaseFingerprint);
    }

    [TestMethod]
    public void AFailureWithoutAFingerprintStillCountsAgainstTheBudget()
    {
        var request = NewRequest(out var formatId);

        request.RecordAutomaticCandidateFailure(formatId, "example-indexer", "c_one", "Failed.", Now);

        Assert.IsNull(request.DeclinedCandidates.Single().ReleaseFingerprint);
        Assert.AreEqual(1, request.CountAutomaticCandidateFailures(formatId, "example-indexer"));
    }
}
