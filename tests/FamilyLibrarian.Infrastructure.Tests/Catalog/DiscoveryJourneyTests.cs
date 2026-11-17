using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// Budget, continuation and expiry invariants of the server-owned ledger. These prove the accounting rules; the real
/// HTTP pipeline must be shown to call them in the A2/A3/A4 handler tests.
/// </summary>
[TestClass]
public sealed class DiscoveryJourneyTests
{
    private static readonly SourceRef HarryPotter = new("hardcover", "5", ExternalReferenceEntityType.Series);
    private static readonly SourceRef Legacy = new("hardcover", "6", ExternalReferenceEntityType.Series);

    private static (DiscoveryJourney Journey, ManualClock Clock, Guid Owner) NewJourney(DiscoveryLimits? limits = null)
    {
        var clock = new ManualClock();
        var owner = Guid.NewGuid();
        return (new DiscoveryJourney(owner, limits ?? DiscoveryLimits.Default, clock), clock, owner);
    }

    private static DiscoveryOperationBudget Begin(DiscoveryJourney journey, SourceRef? subject = null, string provider = "hardcover") =>
        journey.BeginOperation(DiscoveryOperation.GetSeriesMembers, provider, subject ?? HarryPotter, "position")
            ?? throw new AssertFailedException("Operation was not admitted.");

    // ---- attempts, retries, pages ----

    [TestMethod]
    public void AnOperationGetsFiveAttemptsAndTheSixthIsRefusedBeforeDispatch()
    {
        var (journey, _, _) = NewJourney();
        var operation = Begin(journey);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.AreEqual(AttemptAdmission.Admitted, operation.TryAdmitAttempt(false));
        }

        Assert.AreEqual(AttemptAdmission.OperationAttemptsExhausted, operation.TryAdmitAttempt(false));
        Assert.AreEqual(5, operation.Usage.Attempts, "A refused attempt is not counted as dispatched.");
    }

    [TestMethod]
    public void OneRetryIsAllowedAndItConsumesAnAttemptLikeAnyOther()
    {
        var (journey, _, _) = NewJourney();
        var operation = Begin(journey);

        Assert.AreEqual(AttemptAdmission.Admitted, operation.TryAdmitAttempt(false));
        Assert.AreEqual(AttemptAdmission.Admitted, operation.TryAdmitAttempt(true));
        Assert.AreEqual(AttemptAdmission.RetryNotAllowed, operation.TryAdmitAttempt(true), "The transport's own three-retry policy cannot be inherited.");

        var usage = operation.Usage;
        Assert.AreEqual(2, usage.Attempts);
        Assert.AreEqual(1, usage.Retries);
    }

    [TestMethod]
    public void ThePageCeilingIsFivePagesAndContinuationDoesNotResetIt()
    {
        var (journey, _, _) = NewJourney();
        var operation = Begin(journey);

        for (var page = 0; page < 5; page++)
        {
            Assert.IsTrue(operation.TryAdmitPage());
        }

        Assert.IsFalse(operation.TryAdmitPage());
        var issue = journey.IssueContinuation(operation, new DiscoveryCursor("p6"));
        var redemption = journey.Redeem(issue.Token, journey.OwnerId, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position");
        Assert.AreEqual(ContinuationStatus.Accepted, redemption.Status);
        Assert.IsFalse(redemption.Operation!.TryAdmitPage(), "Re-entry through a continuation is the same operation, with the same ceiling.");
    }

    [TestMethod]
    public void TheJourneyCapsAttemptsAcrossItsOperations()
    {
        var (journey, _, _) = NewJourney();
        var admitted = 0;
        AttemptAdmission last = AttemptAdmission.Admitted;

        for (var op = 0; op < 3 && last == AttemptAdmission.Admitted; op++)
        {
            var operation = Begin(journey, new SourceRef("hardcover", $"s{op}", ExternalReferenceEntityType.Series));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                last = operation.TryAdmitAttempt(false);
                if (last != AttemptAdmission.Admitted)
                {
                    break;
                }

                admitted++;
            }
        }

        Assert.AreEqual(12, admitted);
        Assert.AreEqual(AttemptAdmission.JourneyAttemptsExhausted, last);
        Assert.AreEqual(12, journey.Usage.Attempts);
    }

    [TestMethod]
    public void ConcurrentRequestsCannotEachSpendTheSameRemainingAllowance()
    {
        var (journey, _, _) = NewJourney();
        var operation = Begin(journey);
        var admitted = 0;

        Parallel.For(0, 200, _ =>
        {
            if (operation.TryAdmitAttempt(false) == AttemptAdmission.Admitted)
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.AreEqual(5, admitted);
        Assert.AreEqual(5, operation.Usage.Attempts);
    }

    [TestMethod]
    public void CacheHitsAndItemsAreReportedWithoutSpendingAttempts()
    {
        var (journey, _, _) = NewJourney();
        var operation = Begin(journey);

        operation.RecordCacheHit();
        operation.RecordCacheHit();
        operation.RecordItems(40);
        operation.RecordItems(-3);

        var usage = operation.Usage;
        Assert.AreEqual(2, usage.CacheHits);
        Assert.AreEqual(40, usage.Items);
        Assert.AreEqual(0, usage.Attempts);
    }

    // ---- provider-work time ----

    [TestMethod]
    public void TimeSpentOnProviderWorkCountsAndTimeBetweenRequestsDoesNot()
    {
        var (journey, clock, _) = NewJourney();
        var operation = Begin(journey);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(TimeSpan.FromSeconds(15), operation.RemainingWork, "Reading time between POSTs is free.");

        using (operation.BeginWork())
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            Assert.AreEqual(TimeSpan.FromSeconds(9), operation.RemainingWork, "The running scope counts live.");
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(TimeSpan.FromSeconds(9), operation.RemainingWork);
        Assert.AreEqual(TimeSpan.FromSeconds(6), operation.Usage.Elapsed);
    }

    [TestMethod]
    public void TheOperationDeadlinePersistsAcrossContinuationsAndStopsFurtherAttempts()
    {
        var (journey, clock, owner) = NewJourney();
        var operation = Begin(journey);
        using (operation.BeginWork())
        {
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var issue = journey.IssueContinuation(operation, new DiscoveryCursor("p2"));
        var again = journey.Redeem(issue.Token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Operation!;
        using (again.BeginWork())
        {
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.AreEqual(TimeSpan.Zero, again.RemainingWork);
        Assert.AreEqual(AttemptAdmission.DeadlineExceeded, again.TryAdmitAttempt(false));
        Assert.AreEqual(0, again.Usage.Attempts);
    }

    [TestMethod]
    public void TheJourneyCapsTotalProviderWorkAtThirtySecondsAcrossOperations()
    {
        var (journey, clock, _) = NewJourney();
        var first = Begin(journey);
        var second = Begin(journey, Legacy);
        var third = Begin(journey, new SourceRef("hardcover", "7", ExternalReferenceEntityType.Series));

        foreach (var operation in new[] { first, second })
        {
            using (operation.BeginWork())
            {
                clock.Advance(TimeSpan.FromSeconds(14));
            }
        }

        Assert.AreEqual(TimeSpan.FromSeconds(2), third.RemainingWork, "28 of 30 journey seconds are spent; the operation's own 15 are not the limit.");
        using (third.BeginWork())
        {
            clock.Advance(TimeSpan.FromSeconds(3));
        }

        Assert.AreEqual(AttemptAdmission.DeadlineExceeded, third.TryAdmitAttempt(false));
    }

    [TestMethod]
    public void OnlyOneWorkScopeRunsAtATimeAndDisposingTwiceIsHarmless()
    {
        var (journey, clock, _) = NewJourney();
        var first = Begin(journey);
        var second = Begin(journey, Legacy);

        var scope = first.BeginWork();
        Assert.ThrowsExactly<InvalidOperationException>(() => second.BeginWork());
        clock.Advance(TimeSpan.FromSeconds(1));
        scope.Dispose();
        scope.Dispose();
        using (second.BeginWork())
        {
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(TimeSpan.FromSeconds(1), first.Usage.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(1), second.Usage.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(2), journey.Usage.Elapsed);
    }

    // ---- absolute expiry ----

    [TestMethod]
    public void TheJourneyExpiresAbsolutelyAndNeitherReadsNorContinuationsExtendIt()
    {
        var (journey, clock, owner) = NewJourney();
        var operation = Begin(journey);
        var issue = journey.IssueContinuation(operation, new DiscoveryCursor("p2"));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.AreEqual(ContinuationStatus.Accepted, journey.Redeem(issue.Token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status);
        var late = journey.IssueContinuation(operation, new DiscoveryCursor("p3"));
        Assert.AreEqual(ContinuationIssueStatus.Issued, late.Status);
        Assert.AreEqual(journey.ExpiresAtUtc, new DateTimeOffset(2026, 11, 16, 12, 10, 0, TimeSpan.Zero), "Fixed at creation.");

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(journey.IsExpired);
        Assert.AreEqual(ContinuationStatus.Expired, journey.Redeem(late.Token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status);
        Assert.AreEqual(AttemptAdmission.JourneyExpired, operation.TryAdmitAttempt(false));
        Assert.IsFalse(operation.TryAdmitPage());
        Assert.IsNull(journey.BeginOperation(DiscoveryOperation.GetSeriesMembers, "hardcover", Legacy));
        Assert.AreEqual(ContinuationIssueStatus.JourneyExpired, journey.IssueContinuation(operation, new DiscoveryCursor("p4")).Status);
    }

    // ---- continuation scoping ----

    private static (DiscoveryJourney Journey, Guid Owner, string Token) IssueOne()
    {
        var (journey, _, owner) = NewJourney();
        var operation = Begin(journey);
        var issue = journey.IssueContinuation(operation, new DiscoveryCursor("opaque-provider-cursor"));
        Assert.AreEqual(ContinuationIssueStatus.Issued, issue.Status);
        return (journey, owner, issue.Token!);
    }

    [TestMethod]
    public void AMatchingOwnerAndOperationRedeemsTheProviderCursorAndTheTokenIsOpaque()
    {
        var (journey, owner, token) = IssueOne();

        var redemption = journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position");

        Assert.AreEqual(ContinuationStatus.Accepted, redemption.Status);
        Assert.AreEqual("opaque-provider-cursor", redemption.Cursor!.Value);
        Assert.DoesNotContain("opaque-provider-cursor", token);
        Assert.DoesNotContain("hardcover", token);
    }

    [TestMethod]
    public void ForeignAlteredWrongKindAndCrossOperationContinuationsAreRefusedWithoutAnOperation()
    {
        var (journey, owner, token) = IssueOne();

        var cases = new (ContinuationStatus Expected, ContinuationRedemption Actual)[]
        {
            (ContinuationStatus.Foreign, journey.Redeem(token, Guid.NewGuid(), DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position")),
            (ContinuationStatus.Unknown, journey.Redeem(token[..^1] + (token[^1] == '0' ? '1' : '0'), owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position")),
            (ContinuationStatus.Unknown, journey.Redeem(null, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position")),
            (ContinuationStatus.WrongKind, journey.Redeem(token, owner, DiscoveryOperation.GetAuthorWorks, "hardcover", HarryPotter, "position")),
            (ContinuationStatus.CrossOperation, journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "openlibrary", HarryPotter, "position")),
            (ContinuationStatus.CrossOperation, journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", Legacy, "position")),
            (ContinuationStatus.CrossOperation, journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "title")),
            (ContinuationStatus.CrossOperation, journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", null, "position"))
        };

        foreach (var (expected, actual) in cases)
        {
            Assert.AreEqual(expected, actual.Status);
            Assert.IsNull(actual.Operation, "A refused continuation hands back nothing that could dispatch a provider call.");
            Assert.IsNull(actual.Cursor);
        }
    }

    [TestMethod]
    public void ATokenFromAnotherJourneyIsUnknownHere()
    {
        var (_, _, token) = IssueOne();
        var (other, _, otherOwner) = NewJourney();

        Assert.AreEqual(ContinuationStatus.Unknown, other.Redeem(token, otherOwner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status);
    }

    [TestMethod]
    public void AReplayedTokenIsAnotherRequestAndSpendsTheSameAllowance()
    {
        var (journey, owner, token) = IssueOne();

        var admitted = 0;
        for (var replay = 0; replay < 8; replay++)
        {
            var operation = journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Operation!;
            if (operation.TryAdmitAttempt(false) == AttemptAdmission.Admitted)
            {
                admitted++;
            }
        }

        Assert.AreEqual(5, admitted, "Replays cannot mint extra provider calls.");
    }

    [TestMethod]
    public void ARepeatedProviderCursorIsRefusedSoANonAdvancingProviderCannotLoop()
    {
        var (journey, _, _) = NewJourney();
        var operation = Begin(journey);

        Assert.AreEqual(ContinuationIssueStatus.Issued, journey.IssueContinuation(operation, new DiscoveryCursor("page-2")).Status);
        Assert.AreEqual(ContinuationIssueStatus.Issued, journey.IssueContinuation(operation, new DiscoveryCursor("page-3")).Status);
        Assert.AreEqual(ContinuationIssueStatus.RepeatedCursor, journey.IssueContinuation(operation, new DiscoveryCursor("page-2")).Status);

        var other = Begin(journey, Legacy);
        Assert.AreEqual(ContinuationIssueStatus.Issued, journey.IssueContinuation(other, new DiscoveryCursor("page-2")).Status, "Cursor values are scoped per operation.");
    }

    [TestMethod]
    public void ContinuationAndOperationCountsAreBounded()
    {
        var limits = DiscoveryLimits.Default with { MaxContinuationsPerJourney = 2, MaxOperationsPerJourney = 2 };
        var (journey, _, _) = NewJourney(limits);
        var operation = Begin(journey);

        Assert.AreEqual(ContinuationIssueStatus.Issued, journey.IssueContinuation(operation, new DiscoveryCursor("a")).Status);
        Assert.AreEqual(ContinuationIssueStatus.Issued, journey.IssueContinuation(operation, new DiscoveryCursor("b")).Status);
        Assert.AreEqual(ContinuationIssueStatus.CapacityReached, journey.IssueContinuation(operation, new DiscoveryCursor("c")).Status);

        Assert.IsNotNull(journey.BeginOperation(DiscoveryOperation.GetAuthorWorks, "hardcover", Legacy));
        Assert.IsNull(journey.BeginOperation(DiscoveryOperation.GetAuthorWorks, "hardcover", Legacy));
    }

    [TestMethod]
    public void AJourneyNeedsAnOwner()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DiscoveryJourney(Guid.Empty, DiscoveryLimits.Default, new ManualClock()));
    }

    // ---- R5: an operation belongs to exactly one journey ----

    [TestMethod]
    public void AJourneyRefusesToIssueAContinuationForAnotherJourneysOperation()
    {
        var (journeyA, _, ownerA) = NewJourney();
        var (journeyB, _, ownerB) = NewJourney();
        var foreign = Begin(journeyB);

        var issue = journeyA.IssueContinuation(foreign, new DiscoveryCursor("o:99"));

        Assert.AreEqual(ContinuationIssueStatus.ForeignOperation, issue.Status, "Journey A must not mint a token for B's operation.");
        Assert.IsNull(issue.Token);
        var redemptionByA = journeyA.Redeem(issue.Token, ownerA, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position");
        Assert.AreNotEqual(ContinuationStatus.Accepted, redemptionByA.Status);
        Assert.IsNull(redemptionByA.Operation, "B's budget is never handed to A's owner.");
        Assert.AreEqual(0, foreign.Usage.Attempts);
        Assert.AreNotEqual(ownerA, ownerB);
    }

    [TestMethod]
    public void AContinuationIsBoundToTheApprovedFilterAndOrderingAsWellAsTheSubject()
    {
        var (journey, _, owner) = NewJourney();
        var operation = journey.BeginOperation(DiscoveryOperation.GetAuthorWorks, "hardcover", HarryPotter, "book-id", "author-role-non-merged-books")!;
        var token = journey.IssueContinuation(operation, new DiscoveryCursor("o:99")).Token;

        ContinuationStatus Redeem(string? ordering, string? filter) =>
            journey.Redeem(token, owner, DiscoveryOperation.GetAuthorWorks, "hardcover", HarryPotter, ordering, filter).Status;

        Assert.AreEqual(ContinuationStatus.Accepted, Redeem("book-id", "author-role-non-merged-books"));
        Assert.AreEqual(ContinuationStatus.CrossOperation, Redeem("book-id", "all-roles"), "A wider filter is a different scope.");
        Assert.AreEqual(ContinuationStatus.CrossOperation, Redeem("book-id", null), "Omitting the filter does not reset it.");
        Assert.AreEqual(ContinuationStatus.CrossOperation, Redeem("title", "author-role-non-merged-books"));
    }

    [TestMethod]
    public void TwoJourneysOfOneOwnerKeepTheirOperationsApart()
    {
        var owner = Guid.NewGuid();
        var clock = new ManualClock();
        var first = new DiscoveryJourney(owner, DiscoveryLimits.Default, clock);
        var second = new DiscoveryJourney(owner, DiscoveryLimits.Default, clock);
        var inSecond = second.BeginOperation(DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position")!;

        var crossIssue = first.IssueContinuation(inSecond, new DiscoveryCursor("o:99"));
        var ownIssue = second.IssueContinuation(inSecond, new DiscoveryCursor("o:99"));

        Assert.AreNotEqual(ContinuationIssueStatus.Issued, crossIssue.Status);
        Assert.AreEqual(ContinuationIssueStatus.Issued, ownIssue.Status);
        Assert.AreNotEqual(ContinuationStatus.Accepted,
            first.Redeem(ownIssue.Token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status,
            "A token minted by one journey means nothing to its sibling.");
        Assert.AreEqual(ContinuationStatus.Accepted,
            second.Redeem(ownIssue.Token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status);
    }

    [TestMethod]
    public void ForeignOwnersAndReplaysStayBoundToTheOriginalJourneyAndExpiry()
    {
        var (journey, clock, owner) = NewJourney();
        var operation = Begin(journey);
        var token = journey.IssueContinuation(operation, new DiscoveryCursor("o:99")).Token;

        Assert.AreEqual(ContinuationStatus.Foreign,
            journey.Redeem(token, Guid.NewGuid(), DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status);
        var first = journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position");
        var replay = journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position");
        Assert.AreEqual(ContinuationStatus.Accepted, first.Status);
        Assert.AreSame(first.Operation, replay.Operation, "A replay spends the same operation's allowance, never a fresh one.");

        clock.Advance(DiscoveryLimits.Default.Lifetime);
        Assert.AreEqual(ContinuationStatus.Expired,
            journey.Redeem(token, owner, DiscoveryOperation.GetSeriesMembers, "hardcover", HarryPotter, "position").Status);
        Assert.AreEqual(ContinuationIssueStatus.JourneyExpired, journey.IssueContinuation(operation, new DiscoveryCursor("o:198")).Status);
    }
}
