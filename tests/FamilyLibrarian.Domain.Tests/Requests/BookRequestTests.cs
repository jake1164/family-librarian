using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Requests;

/// <summary>
/// The request rules that must hold no matter which caller reaches them.
/// </summary>
[TestClass]
public sealed class BookRequestTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();

    [TestMethod]
    public void ANewRequestStartsPendingWithOneRowPerRequestedFormat()
    {
        var request = Create(RequestMediaType.Ebook, RequestMediaType.Audiobook);

        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
        Assert.IsTrue(request.IsActive);
        CollectionAssert.AreEquivalent(
            new[] { RequestMediaType.Ebook, RequestMediaType.Audiobook },
            request.Formats.Select(format => format.MediaType).ToArray());
        Assert.IsTrue(request.Formats.All(format => format.Status == RequestFormatStatus.Requested));
    }

    [TestMethod]
    public void CreationIsRecordedInStatusHistoryWithNoPriorStatus()
    {
        var request = Create(RequestMediaType.Ebook);

        var entry = request.StatusHistory.Single();
        Assert.IsNull(entry.FromStatus);
        Assert.AreEqual(RequestStatus.PendingAcquisition, entry.ToStatus);
        Assert.AreEqual(UserId, entry.ActorUserId);
        Assert.AreEqual(CreatedAt, entry.OccurredAtUtc);
    }

    [TestMethod]
    public void ARepeatedFormatCollapsesRatherThanCreatingTwoRows()
    {
        var request = Create(RequestMediaType.Ebook, RequestMediaType.Ebook);

        Assert.AreEqual(1, request.Formats.Count);
    }

    [TestMethod]
    public void ARequestWithNoFormatIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BookRequest(UserId, WorkId, [], null, CreatedAt));

    [TestMethod]
    public void AnUnknownMediaTypeIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BookRequest(UserId, WorkId, [(RequestMediaType)99], null, CreatedAt));

    [TestMethod]
    public void ANoteLongerThanTheColumnIsRejectedBeforeItReachesTheDatabase() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BookRequest(
                UserId,
                WorkId,
                [RequestMediaType.Ebook],
                new string('x', BookRequest.MaxNoteLength + 1),
                CreatedAt));

    [TestMethod]
    public void CancellingMovesTheFormatsAndWritesHistory()
    {
        var request = Create(RequestMediaType.Ebook, RequestMediaType.Audiobook);
        var cancelledAt = CreatedAt.AddHours(2);

        request.TransitionTo(RequestStatus.Cancelled, UserId, "Found it elsewhere.", cancelledAt);

        Assert.AreEqual(RequestStatus.Cancelled, request.Status);
        Assert.IsFalse(request.IsActive);
        Assert.AreEqual(cancelledAt, request.StatusChangedAtUtc);
        Assert.IsTrue(request.Formats.All(format => format.Status == RequestFormatStatus.Cancelled));

        var latest = request.StatusHistory.Last();
        Assert.AreEqual(RequestStatus.PendingAcquisition, latest.FromStatus);
        Assert.AreEqual(RequestStatus.Cancelled, latest.ToStatus);
        Assert.AreEqual("Found it elsewhere.", latest.Reason);
    }

    [TestMethod]
    public void ReopeningACancelledRequestReturnsItToTheQueueWithItsFormatsLive()
    {
        var request = Create(RequestMediaType.Ebook);
        request.TransitionTo(RequestStatus.Cancelled, UserId, null, CreatedAt.AddHours(1));

        request.TransitionTo(RequestStatus.PendingAcquisition, UserId, null, CreatedAt.AddHours(2));

        Assert.AreEqual(RequestStatus.PendingAcquisition, request.Status);
        Assert.IsTrue(request.IsActive);
        Assert.IsTrue(request.Formats.All(format => format.Status == RequestFormatStatus.Requested));
        Assert.AreEqual(3, request.StatusHistory.Count);
    }

    [TestMethod]
    public void ReturningAPartlyDeliveredRequestToTheQueueDoesNotReopenAnAlreadyAvailableFormat()
    {
        var request = Create(RequestMediaType.Ebook, RequestMediaType.Audiobook);
        var ebookId = request.Formats.Single(format => format.MediaType == RequestMediaType.Ebook).Id;
        request.MarkFormatAvailable(ebookId, CreatedAt.AddHours(1));
        request.TransitionTo(RequestStatus.NeedsReview, UserId, null, CreatedAt.AddHours(2));

        request.TransitionTo(RequestStatus.PendingAcquisition, UserId, null, CreatedAt.AddHours(3));

        Assert.AreEqual(
            RequestFormatStatus.Available,
            request.Formats.Single(format => format.MediaType == RequestMediaType.Ebook).Status);
        Assert.AreEqual(
            RequestFormatStatus.Requested,
            request.Formats.Single(format => format.MediaType == RequestMediaType.Audiobook).Status);
    }

    [TestMethod]
    public void MakingTheOnlyRequestedFormatAvailableCompletesTheRequestAndRecordsHistory()
    {
        var request = Create(RequestMediaType.Ebook);
        var completedAt = CreatedAt.AddHours(1);

        var completed = request.MarkFormatAvailable(request.Formats.Single().Id, completedAt);

        Assert.IsTrue(completed);
        Assert.AreEqual(RequestStatus.Available, request.Status);
        Assert.AreEqual(RequestFormatStatus.Available, request.Formats.Single().Status);
        Assert.AreEqual(RequestStatus.Available, request.StatusHistory.Last().ToStatus);
    }

    [TestMethod]
    public void ADisallowedTransitionIsRefusedAndChangesNothing()
    {
        var request = Create(RequestMediaType.Ebook);
        request.TransitionTo(RequestStatus.Cancelled, UserId, null, CreatedAt.AddHours(1));

        // Cancelled reopens only to PendingAcquisition; it cannot jump straight to
        // a review or unavailable state.
        Assert.ThrowsExactly<InvalidRequestTransitionException>(() =>
            request.TransitionTo(RequestStatus.NeedsReview, UserId, null, CreatedAt.AddHours(2)));

        Assert.AreEqual(RequestStatus.Cancelled, request.Status);
        Assert.AreEqual(2, request.StatusHistory.Count);
    }

    [TestMethod]
    public void RequestsFormatAnswersOnlyForWhatWasAsked()
    {
        var request = Create(RequestMediaType.Audiobook);

        Assert.IsTrue(request.RequestsFormat(RequestMediaType.Audiobook));
        Assert.IsFalse(request.RequestsFormat(RequestMediaType.Ebook));
    }

    private static BookRequest Create(params RequestMediaType[] mediaTypes) =>
        new(UserId, WorkId, mediaTypes, null, CreatedAt);
}
