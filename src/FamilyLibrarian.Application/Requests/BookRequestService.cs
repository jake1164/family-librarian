using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// The request commands and queries behind My Requests and the request action.
/// </summary>
/// <remarks>
/// Ownership is enforced here, not at the endpoint: every method resolves the
/// caller from <see cref="ICurrentUser"/> and refuses to read or change another
/// user's request. A request that belongs to someone else is reported as not
/// found so the API does not confirm that it exists.
/// </remarks>
public sealed class BookRequestService(
    IRequestRepository repository,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit,
    NotificationService notifications,
    OutboundCommunicationService outboundCommunications,
    IFormatReadinessService readiness,
    IWorkFulfillmentOptionsService fulfillmentOptions)
{
    /// <summary>
    /// The status changes a requester may make. Moving a request to
    /// <see cref="RequestStatus.NeedsReview"/> or
    /// <see cref="RequestStatus.NotAvailable"/> is an administrative judgement and
    /// arrives with the admin queue.
    /// </summary>
    private static readonly RequestStatus[] RequesterTransitions =
    [
        RequestStatus.Cancelled,
        RequestStatus.PendingAcquisition
    ];

    /// <summary>
    /// The status changes an administrator may make manually. <see cref="RequestStatus.Available"/>
    /// is deliberately excluded: it is only ever reached automatically, via
    /// <see cref="BookRequest.MarkFormatAvailable"/> once every requested format is delivered — not
    /// something an admin picks from the transitions list.
    /// </summary>
    private static readonly RequestStatus[] AdminTransitions =
    [
        RequestStatus.PendingAcquisition,
        RequestStatus.NeedsReview,
        RequestStatus.NotAvailable,
        RequestStatus.Cancelled
    ];

    public async Task<CreateBookRequestResult> CreateAsync(
        Guid workId,
        IReadOnlyList<RequestMediaType> mediaTypes,
        string? note,
        bool confirmDuplicate,
        bool confirmOwned,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);

        if (currentUser.UserId is not { } userId)
        {
            return CreateBookRequestResult.Unauthenticated();
        }

        var requestedFormats = mediaTypes.Distinct().ToArray();
        if (requestedFormats.Length == 0)
        {
            return CreateBookRequestResult.Invalid("Choose ebook, audiobook, or both.");
        }

        if (note is { Length: > BookRequest.MaxNoteLength })
        {
            return CreateBookRequestResult.Invalid(
                $"A note may not exceed {BookRequest.MaxNoteLength} characters.");
        }

        if (!await repository.WorkExistsAsync(workId, cancellationToken))
        {
            return CreateBookRequestResult.WorkNotFound();
        }

        foreach (var mediaType in requestedFormats)
        {
            var check = await readiness.CheckAsync(mediaType, cancellationToken);
            if (!check.IsReady)
            {
                return CreateBookRequestResult.Invalid(
                    $"{mediaType} requests aren't available right now: {check.Reason}");
            }
        }

        // Ownership calls out to whatever owned-library providers are
        // registered (e.g. a live CWA/Audiobookshelf lookup), so it is
        // checked here — before the per-(user, workId) creation lock below —
        // and, like the duplicate check, only warns rather than blocks: it is
        // reported first when both an owned match and a duplicate exist,
        // since it is cheaper (no lock) and is the more fundamental "why are
        // you requesting this at all" signal. Confirming past it and
        // resubmitting then lets the duplicate check run normally.
        if (!confirmOwned)
        {
            var owned = new List<OwnedFormatOption>();
            foreach (var mediaType in requestedFormats)
            {
                var options = await fulfillmentOptions.GetOptionsAsync(workId, mediaType, cancellationToken);
                var ownedOption = options.FirstOrDefault(option => option.OptionKind == OptionKind.Owned);
                if (ownedOption is not null)
                {
                    owned.Add(new OwnedFormatOption(mediaType, ownedOption.ProviderId, ownedOption.ExternalActionUri));
                }
            }

            if (owned.Count > 0)
            {
                return CreateBookRequestResult.AlreadyOwned(owned);
            }
        }

        return await repository.InCreateRequestScopeAsync(
            userId,
            workId,
            async token =>
            {
                var existing = await repository.GetActiveRequestsForWorkAsync(
                    userId,
                    workId,
                    token);

                // A repeat request can be legitimate, so an overlap is a warning
                // the user can confirm past, not a rejection.
                var duplicate = existing.FirstOrDefault(request =>
                    requestedFormats.Any(request.RequestsFormat));

                if (duplicate is not null && !confirmDuplicate)
                {
                    var view = await repository.FindViewAsync(duplicate.Id, userId, token);
                    return CreateBookRequestResult.Duplicate(
                        view,
                        requestedFormats
                            .Where(duplicate.RequestsFormat)
                            .ToArray());
                }

                var request = new BookRequest(userId, workId, requestedFormats, note, clock.UtcNow);
                repository.AddRequest(request);
                await repository.SaveChangesAsync(token);

                var created = await repository.FindViewAsync(request.Id, userId, token);
                return CreateBookRequestResult.Created(created!);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<BookRequestView>> ListMineAsync(
        CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? await repository.ListForUserAsync(userId, cancellationToken)
            : [];

    public async Task<BookRequestCommandResult> TransitionAsync(
        Guid requestId,
        RequestStatus to,
        string? reason,
        uint? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BookRequestCommandResult.Unauthenticated();
        }

        if (!Enum.IsDefined(to) || Array.IndexOf(RequesterTransitions, to) < 0)
        {
            return BookRequestCommandResult.Invalid("That status change is not available to you.");
        }

        var request = await repository.FindOwnedRequestAsync(requestId, userId, cancellationToken);
        if (request is null)
        {
            return BookRequestCommandResult.NotFound();
        }

        if (expectedVersion is not null && request.Version != expectedVersion)
        {
            return BookRequestCommandResult.Conflict();
        }

        if (!RequestStatusTransitions.IsAllowed(request.Status, to))
        {
            return BookRequestCommandResult.Invalid(
                $"A request that is {Describe(request.Status)} cannot be {Describe(to)}.");
        }

        request.TransitionTo(to, userId, reason, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);

        var view = await repository.FindViewAsync(requestId, userId, cancellationToken);
        return BookRequestCommandResult.Success(view!);
    }

    /// <summary>Lists requests for the administrative queue.</summary>
    public Task<IReadOnlyList<AdminBookRequestView>> ListForAdminAsync(
        RequestStatus? status,
        CancellationToken cancellationToken) =>
        repository.ListForAdminAsync(status, cancellationToken);

    /// <summary>Loads a request and its status history for administrative review.</summary>
    public Task<AdminBookRequestView?> GetForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        repository.FindAdminViewAsync(requestId, cancellationToken);

    /// <summary>
    /// Applies one transition from the domain matrix as an administrator and
    /// leaves a separate, secret-free audit record of the decision.
    /// </summary>
    public async Task<BookRequestCommandResult> AdminTransitionAsync(
        Guid requestId,
        RequestStatus to,
        string? reason,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BookRequestCommandResult.Unauthenticated();
        }

        if (!Enum.IsDefined(to))
        {
            return BookRequestCommandResult.Invalid("That is not a request status.");
        }

        var request = await repository.FindRequestForAdminAsync(requestId, cancellationToken);
        if (request is null)
        {
            return BookRequestCommandResult.NotFound();
        }

        if (request.Version != expectedVersion)
        {
            return BookRequestCommandResult.Conflict();
        }

        if (!RequestStatusTransitions.IsAllowed(request.Status, to))
        {
            return BookRequestCommandResult.Invalid(
                $"A request that is {Describe(request.Status)} cannot be {Describe(to)}.");
        }

        request.TransitionTo(to, userId, reason, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.BookRequestStatusChanged,
            AuditSubjectTypes.BookRequest,
            requestId.ToString(),
            new { RequestId = requestId, From = request.StatusHistory.Last().FromStatus?.ToString(), To = to.ToString() },
            cancellationToken);

        var view = await repository.FindAdminViewAsync(requestId, cancellationToken);

        if (to == RequestStatus.NeedsReview)
        {
            await notifications.RecordRequestNeedsReviewAsync(requestId, view!.Request.WorkTitle, reason, cancellationToken);
        }
        else if (to is RequestStatus.Available or RequestStatus.NotAvailable)
        {
            await notifications.RecordRequestStatusForUserAsync(
                request.UserId, requestId, view!.Request.WorkTitle, to, cancellationToken);
            await outboundCommunications.EnqueueAsync(
                request.UserId,
                OutboundCommunicationTypes.RequestStatusChanged,
                body: BuildRequestStatusChangedBody(view!.Request.WorkTitle, to),
                subject: $"Family Librarian — {view!.Request.WorkTitle}",
                relatedEntityType: "BookRequest",
                relatedEntityId: requestId,
                cancellationToken);
        }

        return BookRequestCommandResult.Success(view!.Request);
    }

    /// <summary>
    /// Requeues every <see cref="RequestStatus.NeedsReview"/> request — optionally
    /// only ones with a prior lookup against <paramref name="providerId"/> — back
    /// to <see cref="RequestStatus.PendingAcquisition"/>.
    /// </summary>
    /// <remarks>
    /// This reuses the exact same domain transition as the single-request
    /// "return to queue" action rather than re-running provider matching itself:
    /// <see cref="BookRequest.TransitionTo"/> resets <c>StatusChangedAtUtc</c>,
    /// which is what makes the automatic poller (<c>IsCurrentPendingCycleAttempt</c>
    /// in <c>AutomaticRequestFulfillmentService</c>) treat every prior
    /// <see cref="Domain.Acquisition.ProviderAttempt"/> as stale and try every
    /// registered automatic provider again on its next pass — indistinguishable
    /// from a freshly submitted request, with no acquisition logic duplicated here.
    /// </remarks>
    public async Task<BulkRecheckResult> AdminBulkRecheckAsync(
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BulkRecheckResult.Unauthenticated();
        }

        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? null
            : providerId.Trim().ToLowerInvariant();

        var candidates = await repository.ListForManualRecheckAsync(
            RequestStatus.NeedsReview, normalizedProviderId, cancellationToken);

        var reason = normalizedProviderId is null
            ? "Manual recheck against all automatic providers."
            : $"Manual recheck against {normalizedProviderId}.";

        foreach (var request in candidates)
        {
            request.TransitionTo(RequestStatus.PendingAcquisition, userId, reason, clock.UtcNow);
            await audit.WriteAsync(
                AuditActions.BookRequestStatusChanged,
                AuditSubjectTypes.BookRequest,
                request.Id.ToString(),
                new
                {
                    RequestId = request.Id,
                    From = RequestStatus.NeedsReview.ToString(),
                    To = RequestStatus.PendingAcquisition.ToString(),
                    Trigger = "ManualRecheck",
                    ProviderId = normalizedProviderId
                },
                cancellationToken);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return BulkRecheckResult.Success(candidates.Count);
    }

    /// <summary>Changes the administrative note without creating a fake status event.</summary>
    public async Task<BookRequestCommandResult> SetAdminNoteAsync(
        Guid requestId,
        string? note,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BookRequestCommandResult.Unauthenticated();
        }

        if (note?.Trim().Length > BookRequest.MaxAdminNoteLength)
        {
            return BookRequestCommandResult.Invalid(
                $"An admin note may not exceed {BookRequest.MaxAdminNoteLength} characters.");
        }

        var request = await repository.FindRequestForAdminAsync(requestId, cancellationToken);
        if (request is null)
        {
            return BookRequestCommandResult.NotFound();
        }

        if (request.Version != expectedVersion)
        {
            return BookRequestCommandResult.Conflict();
        }

        request.SetAdminNote(note, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.BookRequestNoteChanged,
            AuditSubjectTypes.BookRequest,
            requestId.ToString(),
            new { RequestId = requestId, ChangedBy = userId },
            cancellationToken);

        var view = await repository.FindAdminViewAsync(requestId, cancellationToken);
        return BookRequestCommandResult.Success(view!.Request);
    }

    /// <summary>The transitions the current user may make from a given status.</summary>
    public static IReadOnlyList<RequestStatus> RequesterTransitionsFrom(RequestStatus status) =>
        RequestStatusTransitions.AllowedFrom(status)
            .Where(target => Array.IndexOf(RequesterTransitions, target) >= 0)
            .ToArray();

    /// <summary>The transitions an administrator may make from a given status.</summary>
    public static IReadOnlyList<RequestStatus> AdminTransitionsFrom(RequestStatus status) =>
        RequestStatusTransitions.AllowedFrom(status)
            .Where(target => Array.IndexOf(AdminTransitions, target) >= 0)
            .ToArray();

    private static string Describe(RequestStatus status) => status switch
    {
        RequestStatus.PendingAcquisition => "waiting for the librarian",
        RequestStatus.NeedsReview => "waiting for review",
        RequestStatus.NotAvailable => "marked unavailable",
        RequestStatus.Cancelled => "cancelled",
        _ => status.ToString()
    };

    private static string BuildRequestStatusChangedBody(string workTitle, RequestStatus to) => to switch
    {
        RequestStatus.Available => $"\"{workTitle}\" is available in Family Librarian.",
        RequestStatus.NotAvailable => $"\"{workTitle}\" could not be found and has been marked unavailable.",
        _ => $"\"{workTitle}\" status changed to {Describe(to)}."
    };
}

public sealed record CreateBookRequestResult(
    CreateBookRequestOutcome Outcome,
    BookRequestView? Request,
    IReadOnlyList<RequestMediaType> OverlappingFormats,
    string? Error,
    IReadOnlyList<OwnedFormatOption> OwnedFormats)
{
    public static CreateBookRequestResult Created(BookRequestView request) =>
        new(CreateBookRequestOutcome.Created, request, [], null, []);

    public static CreateBookRequestResult Duplicate(
        BookRequestView? existing,
        IReadOnlyList<RequestMediaType> overlappingFormats) =>
        new(CreateBookRequestOutcome.DuplicateWarning, existing, overlappingFormats, null, []);

    public static CreateBookRequestResult AlreadyOwned(IReadOnlyList<OwnedFormatOption> ownedFormats) =>
        new(CreateBookRequestOutcome.OwnedWarning, null, [], null, ownedFormats);

    public static CreateBookRequestResult WorkNotFound() =>
        new(CreateBookRequestOutcome.WorkNotFound, null, [], null, []);

    public static CreateBookRequestResult Invalid(string error) =>
        new(CreateBookRequestOutcome.Invalid, null, [], error, []);

    public static CreateBookRequestResult Unauthenticated() =>
        new(CreateBookRequestOutcome.Unauthenticated, null, [], null, []);
}

/// <summary>One already-owned format found while creating a request, for the confirmable warning.</summary>
public sealed record OwnedFormatOption(RequestMediaType MediaType, string ProviderId, Uri? ExternalActionUri);

public enum CreateBookRequestOutcome
{
    Created,
    DuplicateWarning,
    OwnedWarning,
    WorkNotFound,
    Invalid,
    Unauthenticated
}

public sealed record BookRequestCommandResult(
    BookRequestCommandOutcome Outcome,
    BookRequestView? Request,
    string? Error)
{
    public static BookRequestCommandResult Success(BookRequestView request) =>
        new(BookRequestCommandOutcome.Success, request, null);

    public static BookRequestCommandResult NotFound() =>
        new(BookRequestCommandOutcome.NotFound, null, null);

    public static BookRequestCommandResult Invalid(string error) =>
        new(BookRequestCommandOutcome.Invalid, null, error);

    public static BookRequestCommandResult Unauthenticated() =>
        new(BookRequestCommandOutcome.Unauthenticated, null, null);

    public static BookRequestCommandResult Conflict() =>
        new(BookRequestCommandOutcome.Conflict, null,
            "Someone else updated this request. Reload it before making another change.");
}

public enum BookRequestCommandOutcome
{
    Success,
    NotFound,
    Invalid,
    Unauthenticated,
    Conflict
}

public sealed record BulkRecheckResult(BulkRecheckOutcome Outcome, int RequeuedCount)
{
    public static BulkRecheckResult Success(int requeuedCount) =>
        new(BulkRecheckOutcome.Success, requeuedCount);

    public static BulkRecheckResult Unauthenticated() =>
        new(BulkRecheckOutcome.Unauthenticated, 0);
}

public enum BulkRecheckOutcome
{
    Success,
    Unauthenticated
}
