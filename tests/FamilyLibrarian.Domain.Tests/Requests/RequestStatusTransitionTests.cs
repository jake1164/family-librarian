using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Requests;

/// <summary>
/// The full transition matrix, asserted move by move.
/// </summary>
/// <remarks>
/// Status changes are the spine of the request workflow and later milestones add
/// states to this table, so every allowed move is named explicitly and every
/// other combination is asserted to be refused.
/// </remarks>
[TestClass]
public sealed class RequestStatusTransitionTests
{
    private static readonly (RequestStatus From, RequestStatus To)[] AllowedMoves =
    [
        (RequestStatus.PendingAcquisition, RequestStatus.NeedsReview),
        (RequestStatus.PendingAcquisition, RequestStatus.NotAvailable),
        (RequestStatus.PendingAcquisition, RequestStatus.Cancelled),
        (RequestStatus.PendingAcquisition, RequestStatus.Available),
        (RequestStatus.NeedsReview, RequestStatus.PendingAcquisition),
        (RequestStatus.NeedsReview, RequestStatus.NotAvailable),
        (RequestStatus.NeedsReview, RequestStatus.Cancelled),
        (RequestStatus.NeedsReview, RequestStatus.Available),
        (RequestStatus.NotAvailable, RequestStatus.PendingAcquisition),
        (RequestStatus.Cancelled, RequestStatus.PendingAcquisition)
    ];

    [TestMethod]
    public void EveryDocumentedMoveIsAllowed()
    {
        foreach (var (from, to) in AllowedMoves)
        {
            Assert.IsTrue(
                RequestStatusTransitions.IsAllowed(from, to),
                $"{from} -> {to} should be allowed.");
        }
    }

    [TestMethod]
    public void EveryOtherMoveIsRefused()
    {
        var statuses = Enum.GetValues<RequestStatus>();

        foreach (var from in statuses)
        {
            foreach (var to in statuses)
            {
                if (AllowedMoves.Contains((from, to)))
                {
                    continue;
                }

                Assert.IsFalse(
                    RequestStatusTransitions.IsAllowed(from, to),
                    $"{from} -> {to} should be refused.");
            }
        }
    }

    [TestMethod]
    public void AStatusNeverTransitionsToItself()
    {
        foreach (var status in Enum.GetValues<RequestStatus>())
        {
            Assert.IsFalse(
                RequestStatusTransitions.IsAllowed(status, status),
                $"{status} -> {status} should be refused.");
        }
    }

    [TestMethod]
    [DataRow(RequestStatus.PendingAcquisition, true)]
    [DataRow(RequestStatus.NeedsReview, true)]
    [DataRow(RequestStatus.NotAvailable, false)]
    [DataRow(RequestStatus.Cancelled, false)]
    [DataRow(RequestStatus.Available, false)]
    public void OutstandingStatusesAreTheOnesDuplicateDetectionCountsAgainst(
        RequestStatus status,
        bool expected) =>
        Assert.AreEqual(expected, RequestStatusTransitions.IsActive(status));
}
