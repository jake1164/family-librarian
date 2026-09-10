using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Delivery;

/// <summary>
/// The automatic release trigger, the existing-book fast path, and the retry
/// sweep behind Kindle delivery -- KINDLE-5.
/// </summary>
[TestClass]
public sealed class DeliveryAttemptServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ReleaseCreatesOneAttemptPerOptedInActiveParticipant()
    {
        var context = new TestContext();
        var wantsDelivery = context.SeedEnabledTarget();
        var noDelivery = Guid.NewGuid();
        var withdrawn = context.SeedEnabledTarget();
        var request = new BookRequest(
            wantsDelivery.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: wantsDelivery.Id);
        request.Join(noDelivery, [RequestMediaType.Ebook], null, Now);
        request.Join(withdrawn.UserId, [RequestMediaType.Ebook], null, Now, deliveryTargetId: withdrawn.Id);
        request.Withdraw(withdrawn.UserId, Now);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        var attempt = context.DeliveryAttempts.Rows.Single();
        Assert.AreEqual(wantsDelivery.UserId, attempt.UserId);
        Assert.AreEqual("42", attempt.ExternalBookId);
        Assert.AreEqual("epub", attempt.BookFormat);
        Assert.IsFalse(attempt.Convert);
    }

    /// <summary>
    /// MediaAsset.Format carries Path.GetExtension's leading dot (e.g.
    /// ".epub"). CWA's /send_selected route matches book_format against
    /// Calibre's stored format token, which is always bare (e.g. "EPUB") --
    /// an unstripped dot never matches, and CWA reports that mismatch as a
    /// generic "could not be read" file error. Confirmed against a real
    /// send on toontown-int-srv2, 2026-09-09.
    /// </summary>
    [TestMethod]
    public async Task ReleaseStripsLeadingDotFromBookFormat()
    {
        var context = new TestContext();
        var wantsDelivery = context.SeedEnabledTarget();
        var request = new BookRequest(
            wantsDelivery.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: wantsDelivery.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", ".epub", Now, CancellationToken.None);

        var attempt = context.DeliveryAttempts.Rows.Single();
        Assert.AreEqual("epub", attempt.BookFormat);
    }

    [TestMethod]
    public async Task ReleasingTwiceForTheSameRequestIsANoOp()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);
        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        Assert.HasCount(1, context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task ADisabledDeliveryTargetIsSkipped()
    {
        var context = new TestContext();
        var target = new DeliveryTarget(Guid.NewGuid(), DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.com", Now);
        target.SetEnabled(false, Now);
        context.DeliveryTargets.Add(target);
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        Assert.IsEmpty(context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task ASuccessfulDeliverySubmitsTheAttempt()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");
        var target = context.SeedEnabledTarget();
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        var attempt = context.DeliveryAttempts.Rows.Single();
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, attempt.Status);
        Assert.IsFalse(attempt.IsRetryable);
    }

    [TestMethod]
    public async Task ATransportFailureIsMarkedRetryable()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.TransportFailure("timed out");
        var target = context.SeedEnabledTarget();
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        var attempt = context.DeliveryAttempts.Rows.Single();
        Assert.AreEqual(DeliveryAttemptStatus.Failed, attempt.Status);
        Assert.IsTrue(attempt.IsRetryable);
    }

    [TestMethod]
    public async Task ARejectionIsMarkedNotRetryable()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.Rejected("SMTP not configured");
        var target = context.SeedEnabledTarget();
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        var attempt = context.DeliveryAttempts.Rows.Single();
        Assert.AreEqual(DeliveryAttemptStatus.Failed, attempt.Status);
        Assert.IsFalse(attempt.IsRetryable);
    }

    [TestMethod]
    public async Task SendingAnExistingOwnedBookSucceeds()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var workId = Guid.NewGuid();
        context.OwnedLibrary.Owned[workId] = "book-7";

        var result = await context.Service.SendExistingBookAsync(workId, CancellationToken.None);

        Assert.AreEqual(SendExistingBookOutcome.Success, result.Outcome);
        var attempt = context.DeliveryAttempts.Rows.Single();
        Assert.IsNull(attempt.RequestId);
        Assert.AreEqual("book-7", attempt.ExternalBookId);
        Assert.AreEqual("epub", attempt.BookFormat);
        Assert.IsTrue(attempt.Convert);
    }

    [TestMethod]
    public async Task SendingABookNotInTheLibraryReportsNotOwned()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);

        var result = await context.Service.SendExistingBookAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(SendExistingBookOutcome.NotOwned, result.Outcome);
        Assert.IsEmpty(context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task SendingWithoutAConfiguredTargetReportsNotConfigured()
    {
        var context = new TestContext();
        context.CurrentUser.SetUser(Guid.NewGuid());
        var workId = Guid.NewGuid();
        context.OwnedLibrary.Owned[workId] = "book-7";

        var result = await context.Service.SendExistingBookAsync(workId, CancellationToken.None);

        Assert.AreEqual(SendExistingBookOutcome.TargetNotConfigured, result.Outcome);
    }

    [TestMethod]
    public async Task SendingWhileSignedOutIsUnauthenticated()
    {
        var context = new TestContext();

        var result = await context.Service.SendExistingBookAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(SendExistingBookOutcome.Unauthenticated, result.Outcome);
    }

    [TestMethod]
    public async Task RetryPastCooldownCreatesANewRowRatherThanReopeningTheOldOne()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var requestId = Guid.NewGuid();
        var failed = new DeliveryAttempt(
            requestId, target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "timeout", retryable: true);
        context.DeliveryAttempts.Add(failed);

        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");
        context.Clock.UtcNow = Now.AddMinutes(3);

        var retried = await context.Service.RetryFailedAsync(CancellationToken.None);

        Assert.AreEqual(1, retried);
        Assert.HasCount(2, context.DeliveryAttempts.Rows);
        Assert.AreEqual(DeliveryAttemptStatus.Failed, failed.Status);
        var retry = context.DeliveryAttempts.Rows.Single(row => row.Id != failed.Id);
        Assert.AreEqual(2, retry.AttemptNumber);
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, retry.Status);
        Assert.AreEqual(requestId, retry.RequestId);
    }

    [TestMethod]
    public async Task RetryLeavesAFailureUntouchedBeforeItsCooldownElapses()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var failed = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "timeout", retryable: true);
        context.DeliveryAttempts.Add(failed);
        context.Clock.UtcNow = Now.AddSeconds(30);

        var retried = await context.Service.RetryFailedAsync(CancellationToken.None);

        Assert.AreEqual(0, retried);
        Assert.HasCount(1, context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task RetryNeverPicksUpANonRetryableFailure()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var failed = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "rejected", retryable: false);
        context.DeliveryAttempts.Add(failed);
        context.Clock.UtcNow = Now.AddHours(1);

        var retried = await context.Service.RetryFailedAsync(CancellationToken.None);

        Assert.AreEqual(0, retried);
    }

    [TestMethod]
    public async Task AUserInitiatedRetryBypassesCooldownAndRetryability()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var failed = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        // Non-retryable and completed moments ago -- the automatic sweep would
        // never touch this row, but an explicit user request still can.
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "not configured", retryable: false);
        context.DeliveryAttempts.Add(failed);
        context.Clock.UtcNow = Now.AddSeconds(5);
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");

        var result = await context.Service.RetryAsync(failed.Id, CancellationToken.None);

        Assert.AreEqual(RetryDeliveryOutcome.Success, result.Outcome);
        Assert.HasCount(2, context.DeliveryAttempts.Rows);
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, result.Attempt!.Status);
        Assert.AreEqual(2, result.Attempt!.AttemptNumber);
    }

    [TestMethod]
    public async Task AUserCannotRetryAnotherUsersAttempt()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(Guid.NewGuid());
        var failed = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "timeout", retryable: true);
        context.DeliveryAttempts.Add(failed);

        var result = await context.Service.RetryAsync(failed.Id, CancellationToken.None);

        Assert.AreEqual(RetryDeliveryOutcome.NotFound, result.Outcome);
        Assert.HasCount(1, context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task ANotYetFailedAttemptCannotBeRetried()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var pending = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        context.DeliveryAttempts.Add(pending);

        var result = await context.Service.RetryAsync(pending.Id, CancellationToken.None);

        Assert.AreEqual(RetryDeliveryOutcome.NotFailed, result.Outcome);
    }

    [TestMethod]
    public async Task RetryingWhileSignedOutIsUnauthenticated()
    {
        var context = new TestContext();

        var result = await context.Service.RetryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(RetryDeliveryOutcome.Unauthenticated, result.Outcome);
    }

    [TestMethod]
    public async Task AdminRetrySucceedsRegardlessOfOwnership()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var failed = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "timeout", retryable: false);
        context.DeliveryAttempts.Add(failed);
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");

        var succeeded = await context.Service.AdminRetryAsync(failed.Id, CancellationToken.None);

        Assert.IsTrue(succeeded);
        Assert.HasCount(2, context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task AdminRetryOnANonFailedAttemptDoesNothing()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var pending = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        context.DeliveryAttempts.Add(pending);

        var succeeded = await context.Service.AdminRetryAsync(pending.Id, CancellationToken.None);

        Assert.IsFalse(succeeded);
        Assert.HasCount(1, context.DeliveryAttempts.Rows);
    }

    [TestMethod]
    public async Task ASuccessfulSubmissionRecordsAConfirmationRequestNotification()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");
        var target = context.SeedEnabledTarget();
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(
            request, "42", "epub", Now, CancellationToken.None, workTitle: "Debt of Honor");

        var attempt = context.DeliveryAttempts.Rows.Single();
        var notification = context.NotificationRepository.Added.Single();
        Assert.AreEqual(NotificationCategories.KindleDeliveryConfirmationRequested, notification.Category);
        Assert.AreEqual(target.UserId, notification.RecipientUserId);
        Assert.AreEqual(attempt.Id.ToString(), notification.SubjectId);
        Assert.Contains("Debt of Honor", notification.Title);
    }

    [TestMethod]
    public async Task AFailedSubmissionDoesNotRecordAConfirmationRequestNotification()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.Rejected("not configured");
        var target = context.SeedEnabledTarget();
        var request = new BookRequest(
            target.UserId, Guid.NewGuid(), [RequestMediaType.Ebook], null, Now, deliveryTargetId: target.Id);

        await context.Service.ReleaseForRequestFormatAsync(request, "42", "epub", Now, CancellationToken.None);

        Assert.IsEmpty(context.NotificationRepository.Added);
    }

    [TestMethod]
    public async Task SendingAnExistingOwnedBookCapturesItsTitleFromTheCatalog()
    {
        var context = new TestContext();
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var workId = Guid.NewGuid();
        context.OwnedLibrary.Owned[workId] = "book-7";
        context.CatalogRepository.Seed(workId, TestContext.SeedWork("Red Storm Rising"));

        var result = await context.Service.SendExistingBookAsync(workId, CancellationToken.None);

        Assert.AreEqual(SendExistingBookOutcome.Success, result.Outcome);
        Assert.AreEqual("Red Storm Rising", result.Attempt!.BookTitle);
    }

    [TestMethod]
    public async Task ConfirmingReceiptOnASubmittedAttemptSucceeds()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var submitted = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitted, Now);
        context.DeliveryAttempts.Add(submitted);

        var result = await context.Service.ConfirmReceivedAsync(submitted.Id, CancellationToken.None);

        Assert.AreEqual(ConfirmDeliveryOutcome.Success, result.Outcome);
        Assert.AreEqual(DeliveryConfirmationStatus.Confirmed, submitted.ConfirmationStatus);
    }

    [TestMethod]
    public async Task ReportingAPendingAttemptMissingIsRejected()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var pending = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        context.DeliveryAttempts.Add(pending);

        var result = await context.Service.ReportMissingAsync(pending.Id, CancellationToken.None);

        Assert.AreEqual(ConfirmDeliveryOutcome.NotSubmitted, result.Outcome);
    }

    [TestMethod]
    public async Task AUserCannotConfirmAnotherUsersAttempt()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(Guid.NewGuid());
        var submitted = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitted, Now);
        context.DeliveryAttempts.Add(submitted);

        var result = await context.Service.ConfirmReceivedAsync(submitted.Id, CancellationToken.None);

        Assert.AreEqual(ConfirmDeliveryOutcome.NotFound, result.Outcome);
    }

    /// <summary>
    /// KINDLE-7: a submitted-but-unreceived attempt is not itself "Failed", so
    /// a retry must widen past the Failed-only gate that governs an ordinary
    /// send failure.
    /// </summary>
    [TestMethod]
    public async Task RetryingAfterReportedMissingSendsANewAttempt()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var submitted = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitted, Now);
        submitted.ReportMissing(Now);
        context.DeliveryAttempts.Add(submitted);
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");

        var result = await context.Service.RetryAsync(submitted.Id, CancellationToken.None);

        Assert.AreEqual(RetryDeliveryOutcome.Success, result.Outcome);
        Assert.HasCount(2, context.DeliveryAttempts.Rows);
        Assert.AreEqual(2, result.Attempt!.AttemptNumber);
    }

    /// <summary>
    /// F9: an administrator can perform the same "it never arrived" resend a
    /// user can -- mirroring <see cref="RetryingAfterReportedMissingSendsANewAttempt"/>
    /// but through the admin-only entry point, which previously only accepted
    /// <see cref="DeliveryAttemptStatus.Failed"/>.
    /// </summary>
    [TestMethod]
    public async Task AdminRetrySucceedsOnASubmittedAttemptReportedMissing()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        var submitted = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        submitted.TransitionTo(DeliveryAttemptStatus.Submitted, Now);
        submitted.ReportMissing(Now);
        context.DeliveryAttempts.Add(submitted);
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");

        var succeeded = await context.Service.AdminRetryAsync(submitted.Id, CancellationToken.None);

        Assert.IsTrue(succeeded);
        Assert.HasCount(2, context.DeliveryAttempts.Rows);
    }

    /// <summary>
    /// F2 regression: a target disabled after a Pending row was created (or
    /// between a failure and its retry) must be re-checked immediately before
    /// dispatch, not only when the release/existing-book paths first create
    /// the row.
    /// </summary>
    [TestMethod]
    public async Task RetryingWithADisabledTargetFailsWithoutCallingTheProvider()
    {
        var context = new TestContext();
        var target = context.SeedEnabledTarget();
        context.CurrentUser.SetUser(target.UserId);
        var failed = new DeliveryAttempt(
            Guid.NewGuid(), target.UserId, target.Id, "cwa", "1", "epub", false, attemptNumber: 1, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        failed.TransitionTo(DeliveryAttemptStatus.Failed, Now, "timeout", retryable: true);
        context.DeliveryAttempts.Add(failed);
        target.SetEnabled(false, Now);
        context.Provider.NextOutcome = EbookDeliveryOutcome.Delivered("Sent.");

        var result = await context.Service.RetryAsync(failed.Id, CancellationToken.None);

        Assert.AreEqual(RetryDeliveryOutcome.Success, result.Outcome);
        Assert.AreEqual(DeliveryAttemptStatus.Failed, result.Attempt!.Status);
        Assert.AreEqual("The delivery target is disabled.", result.Attempt!.FailureReason);
        Assert.AreEqual(0, context.Provider.DeliverCallCount);
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Clock = new MutableClock { UtcNow = Now };
            CurrentUser = new StubCurrentUser();
            Provider = new StubEbookDeliveryProvider();
            OwnedLibrary = new StubOwnedLibraryProvider();
            CatalogRepository = new StubCatalogRepository();
            NotificationRepository = new RecordingNotificationRepository();
            Notifications = new NotificationService(NotificationRepository, CurrentUser, Clock);
            Service = new DeliveryAttemptService(
                DeliveryAttempts, DeliveryTargets, [Provider], [OwnedLibrary], CurrentUser, new NoOpAuditWriter(),
                Clock, CatalogRepository, Notifications);
        }

        public InMemoryDeliveryAttemptRepository DeliveryAttempts { get; } = new();

        public InMemoryDeliveryTargetRepository DeliveryTargets { get; } = new();

        public StubEbookDeliveryProvider Provider { get; }

        public StubOwnedLibraryProvider OwnedLibrary { get; }

        public StubCatalogRepository CatalogRepository { get; }

        public RecordingNotificationRepository NotificationRepository { get; }

        public NotificationService Notifications { get; }

        public StubCurrentUser CurrentUser { get; }

        public MutableClock Clock { get; }

        public DeliveryAttemptService Service { get; }

        public DeliveryTarget SeedEnabledTarget()
        {
            var target = new DeliveryTarget(
                Guid.NewGuid(), DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.com", Now);
            DeliveryTargets.Add(target);
            return target;
        }

        public static Work SeedWork(string title) =>
            new(title, description: null, coverUrl: null, firstPublicationDate: null, PublicationStatus.Published, Now);
    }

    private sealed class StubEbookDeliveryProvider : IEbookDeliveryProvider
    {
        public string Id => "cwa";

        public EbookDeliveryOutcome NextOutcome { get; set; } = EbookDeliveryOutcome.Delivered("Sent.");

        public int DeliverCallCount { get; private set; }

        public Task<bool> CanDeliverAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<EbookDeliveryOutcome> DeliverAsync(
            string providerBookId, string bookFormat, bool convert, string recipientEmail,
            CancellationToken cancellationToken)
        {
            DeliverCallCount++;
            return Task.FromResult(NextOutcome);
        }

        public Task<ConnectionTestOutcome> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestOutcome(true, "ok"));
    }

    private sealed class StubOwnedLibraryProvider : IOwnedLibraryProvider
    {
        public string Id => "cwa";

        public Dictionary<Guid, string> Owned { get; } = [];

        public Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FulfillmentOption>>(
                Owned.TryGetValue(workId, out var bookId) ? [BuildOption(bookId)] : []);

        public Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
            BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);

        private FulfillmentOption BuildOption(string bookId) => new(
            ProviderId: Id,
            ProviderResultId: bookId,
            WorkId: Guid.Empty,
            EditionId: null,
            MediaType: RequestMediaType.Ebook,
            OptionKind: OptionKind.Owned,
            AcquisitionMethod: AcquisitionMethod.OwnedImport,
            Format: null,
            Language: null,
            Quality: null,
            Availability: null,
            Cost: null,
            Currency: null,
            LicenseOrUsageStatus: null,
            DrmStatus: null,
            ExternalActionUri: null,
            ProviderData: null);
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        private Guid? userId;

        public void SetUser(Guid id) => userId = id;

        public Guid? UserId => userId;

        public string? DisplayName => userId is null ? null : "Reader";
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryDeliveryAttemptRepository : IDeliveryAttemptRepository
    {
        public List<DeliveryAttempt> Rows { get; } = [];

        public Task<DeliveryAttempt?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(row => row.Id == id));

        public Task<IReadOnlyList<DeliveryAttempt>> ListForRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryAttempt>>(Rows.Where(row => row.RequestId == requestId).ToArray());

        public Task<IReadOnlyList<DeliveryAttempt>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryAttempt>>(Rows.Where(row => row.UserId == userId).ToArray());

        public Task<IReadOnlyList<DeliveryAttemptView>> ListRecentAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<DeliveryAttempt>> ListRetryableFailedAsync(
            DateTimeOffset olderThanUtc, int maxAttemptNumber, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryAttempt>>(Rows.Where(row =>
                row.Status == DeliveryAttemptStatus.Failed &&
                row.IsRetryable &&
                row.AttemptNumber < maxAttemptNumber &&
                row.CompletedAtUtc is not null &&
                row.CompletedAtUtc <= olderThanUtc).ToArray());

        public void Add(DeliveryAttempt attempt) => Rows.Add(attempt);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryDeliveryTargetRepository : IDeliveryTargetRepository
    {
        public List<DeliveryTarget> Rows { get; } = [];

        public Task<DeliveryTarget?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(row => row.Id == id));

        public Task<IReadOnlyList<DeliveryTarget>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryTarget>>(Rows.Where(row => row.UserId == userId).ToArray());

        public Task<IReadOnlyList<DeliveryTarget>> ListAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliveryTarget>>(Rows.ToArray());

        public void Add(DeliveryTarget target) => Rows.Add(target);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubCatalogRepository : ICatalogRepository
    {
        private readonly Dictionary<Guid, Work> _works = [];

        public void Seed(Guid workId, Work work) => _works[workId] = work;

        public Task<Work?> FindWorkByExternalReferenceAsync(
            string providerId, string externalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Work?> FindWorkByIsbn13Async(
            IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(_works.GetValueOrDefault(workId));

        public Task<IReadOnlyList<Domain.Catalog.ExternalReference>> GetWorkSourcesAsync(
            Guid workId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Author?> FindAuthorByNormalizedNameAsync(
            string normalizedName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Series?> FindSeriesByNormalizedNameAsync(
            string normalizedName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void AddWork(Work work) => _works[work.Id] = work;

        public void AddAuthor(Author author) => throw new NotSupportedException("Not exercised by these tests.");

        public void AddSeries(Series series) => throw new NotSupportedException("Not exercised by these tests.");

        public void AddExternalReference(Domain.Catalog.ExternalReference externalReference) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingNotificationRepository : INotificationRepository
    {
        public List<NotificationEvent> Added { get; } = [];

        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience,
            Guid? recipientUserId,
            string category,
            string? subjectType,
            string? subjectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Added.SingleOrDefault(evt =>
                evt.Audience == audience && evt.RecipientUserId == recipientUserId &&
                evt.Category == category && evt.SubjectType == subjectType && evt.SubjectId == subjectId));

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
        {
            Added.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NotificationReceipt>>([]);

        public Task RemoveReceiptsAsync(IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>>([]);

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<NotificationReceipt?>(null);

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
