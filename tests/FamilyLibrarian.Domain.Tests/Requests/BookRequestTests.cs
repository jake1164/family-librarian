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

    [TestMethod]
    public void RequestingDeliveryAtCreationRecordsItOnTheOwningParticipant()
    {
        var deliveryTargetId = Guid.NewGuid();

        var request = new BookRequest(UserId, WorkId, [RequestMediaType.Ebook], null, CreatedAt, deliveryTargetId);

        var participant = request.Participants.Single();
        Assert.AreEqual(deliveryTargetId, participant.DeliveryTargetId);
    }

    [TestMethod]
    public void DeliveryIsNotRequestedByDefault()
    {
        var request = Create(RequestMediaType.Ebook);

        Assert.IsNull(request.Participants.Single().DeliveryTargetId);
    }

    [TestMethod]
    public void RequestingDeliveryWithoutTheEbookFormatIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BookRequest(UserId, WorkId, [RequestMediaType.Audiobook], null, CreatedAt, Guid.NewGuid()));

    [TestMethod]
    public void JoinCanAddDeliveryIntentForANewParticipant()
    {
        var request = Create(RequestMediaType.Ebook);
        var joiningUserId = Guid.NewGuid();
        var deliveryTargetId = Guid.NewGuid();

        request.Join(joiningUserId, [RequestMediaType.Ebook], null, CreatedAt.AddHours(1), deliveryTargetId);

        var participant = request.Participants.Single(candidate => candidate.UserId == joiningUserId);
        Assert.AreEqual(deliveryTargetId, participant.DeliveryTargetId);
    }

    [TestMethod]
    public void RejoiningReplacesThePreviouslyRequestedDeliveryTarget()
    {
        var request = Create(RequestMediaType.Ebook);
        var firstTargetId = Guid.NewGuid();
        request.Join(UserId, [RequestMediaType.Ebook], null, CreatedAt.AddHours(1), firstTargetId);

        request.Join(UserId, [RequestMediaType.Ebook], null, CreatedAt.AddHours(2), deliveryTargetId: null);

        Assert.IsNull(request.Participants.Single().DeliveryTargetId);
    }

    /// <summary>
    /// F6 regression: joining again to add a *different* format the caller
    /// never mentions delivery for must not erase a Kindle target already
    /// set for the ebook -- unlike <see cref="RejoiningReplacesThePreviouslyRequestedDeliveryTarget"/>,
    /// which re-specifies the ebook format itself and is therefore authoritative.
    /// </summary>
    [TestMethod]
    public void AddingAnAudiobookFormatPreservesTheExistingEbookDeliveryTarget()
    {
        var request = Create(RequestMediaType.Ebook);
        var targetId = Guid.NewGuid();
        request.Join(UserId, [RequestMediaType.Ebook], null, CreatedAt.AddHours(1), targetId);

        request.Join(UserId, [RequestMediaType.Audiobook], null, CreatedAt.AddHours(2), deliveryTargetId: null);

        var participant = request.Participants.Single();
        Assert.AreEqual(targetId, participant.DeliveryTargetId);
        Assert.IsTrue(participant.WantsEbook);
        Assert.IsTrue(participant.WantsAudiobook);
    }

    private static BookRequest Create(params RequestMediaType[] mediaTypes) =>
        new(UserId, WorkId, mediaTypes, null, CreatedAt);
}
