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
/// Requester access is based on membership. A caller may join a shared request
/// through its Work, but cannot read private notes or withdraw another person's
/// interest. Nonmembers receive not-found from requester-specific endpoints.
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
        CancellationToken cancellationToken,
        string? versionKind = null,
        string? versionDetails = null)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);

        if (currentUser.UserId is not { } userId)
        {
            return CreateBookRequestResult.Unauthenticated();
        }

        var requestedFormats = mediaTypes.Distinct().ToArray();
        if (requestedFormats.Length == 0 || requestedFormats.Any(format => !Enum.IsDefined(format)))
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

        var isVersionRequest = !string.IsNullOrWhiteSpace(versionKind);
        if (isVersionRequest && (versionKind is not ("Language" or "Edition" or "Narration" or "Accessibility" or "Replacement") ||
            string.IsNullOrWhiteSpace(versionDetails) || versionDetails.Trim().Length > BookRequest.MaxNoteLength))
        {
            return CreateBookRequestResult.Invalid("Choose the difference and describe the specific version needed (up to 1,000 characters).");
        }
        if ((confirmDuplicate || confirmOwned) && !isVersionRequest)
        {
            return CreateBookRequestResult.Invalid("To request another copy, specify a language, edition, narration, accessibility need, or replacement reason for librarian review.");
        }

        return await repository.InCreateRequestScopeAsync(
            userId,
            workId,
            async token =>
            {
                var existing = await repository.GetActiveRequestsForWorkAsync(userId, workId, token);
                var shared = existing.FirstOrDefault(request => isVersionRequest
                    ? request.RequiresManualFulfillment && request.VersionKind == versionKind &&
                      string.Equals(request.VersionDetails, versionDetails!.Trim(), StringComparison.OrdinalIgnoreCase)
                    : !request.RequiresManualFulfillment);
                if (!isVersionRequest)
                {
                    var owned = new List<OwnedFormatOption>();
                    foreach (var mediaType in requestedFormats.Where(format => shared is null || !shared.RequestsFormat(format)))
                    {
                        var options = await fulfillmentOptions.GetOptionsAsync(workId, mediaType, token);
                        var match = options.FirstOrDefault(option => option.OptionKind == OptionKind.Owned);
                        if (match is not null)
                            owned.Add(new OwnedFormatOption(mediaType, match.ProviderId, match.ExternalActionUri));
                    }
                    if (owned.Count > 0) return CreateBookRequestResult.AlreadyOwned(owned);
                }

                if (!isVersionRequest)
                {
                    foreach (var mediaType in requestedFormats.Where(format => shared is null || !shared.RequestsFormat(format)))
                    {
                        var check = await readiness.CheckAsync(mediaType, token);
                        if (!check.IsReady)
                            return CreateBookRequestResult.Invalid($"{mediaType} requests aren't available right now: {check.Reason}");
                    }
                }

                if (shared is not null)
                {
                    shared.Join(userId, requestedFormats, note, clock.UtcNow);
                    await repository.SaveChangesAsync(token);
                    var joined = await repository.FindViewAsync(shared.Id, userId, token);
                    return CreateBookRequestResult.Created(joined!);
                }

                var request = new BookRequest(userId, workId, requestedFormats, note, clock.UtcNow);
                if (isVersionRequest)
                    request.RequireVersionReview(versionKind!, versionDetails!, userId, clock.UtcNow);
                repository.AddRequest(request);
                await repository.SaveChangesAsync(token);

                var created = await repository.FindViewAsync(request.Id, userId, token);
                if (isVersionRequest)
                    await notifications.RecordRequestNeedsReviewAsync(request.Id, created!.WorkTitle, "A specific version was requested.", token);
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
            return BookRequestCommandResult.Unauthenticated();
        if (to is not (RequestStatus.Cancelled or RequestStatus.PendingAcquisition))
            return BookRequestCommandResult.Invalid("That change is not available to a requester.");
        var initial = await repository.FindViewAsync(requestId, userId, cancellationToken);
        if (initial is null) return BookRequestCommandResult.NotFound();

        return await repository.InCreateRequestScopeAsync(userId, initial.WorkId, async token =>
        {
            var request = await repository.FindOwnedRequestAsync(requestId, userId, token);
            if (request is null) return BookRequestCommandResult.NotFound();
            if (expectedVersion is not null && request.Version != expectedVersion)
                return BookRequestCommandResult.Conflict();
            if (to == RequestStatus.Cancelled)
            {
                if (!request.IsActive)
                    return BookRequestCommandResult.Invalid("This request is already closed.");
                request.Withdraw(userId, clock.UtcNow);
            }
            else
            {
                if (request.RequiresManualFulfillment)
                    return BookRequestCommandResult.Invalid("Submit the specific version from the book page for librarian review.");
                var participant = request.Participants.Single(member => member.UserId == userId);
                if (participant.WithdrawnAtUtc is null && request.IsActive)
                    return BookRequestCommandResult.Invalid("You already participate in this request.");
                if (!request.IsActive && !RequestStatusTransitions.IsAllowed(request.Status, to))
                    return BookRequestCommandResult.Invalid("This completed request cannot be reopened.");
                var active = await repository.GetActiveRequestsForWorkAsync(userId, request.WorkId, token);
                var shared = active.FirstOrDefault(candidate => !candidate.RequiresManualFulfillment);
                var formats = new List<RequestMediaType>();
                if (participant.WantsEbook) formats.Add(RequestMediaType.Ebook);
                if (participant.WantsAudiobook) formats.Add(RequestMediaType.Audiobook);
                foreach (var mediaType in formats.Where(format => shared is null || !shared.RequestsFormat(format)))
                {
                    var options = await fulfillmentOptions.GetOptionsAsync(request.WorkId, mediaType, token);
                    if (options.Any(option => option.OptionKind == OptionKind.Owned))
                        return BookRequestCommandResult.Invalid("This format is already in the library. Use the book page if a different version is needed.");
                }
                if (shared is null)
                {
                    request.TransitionTo(RequestStatus.PendingAcquisition, userId, reason, clock.UtcNow);
                    shared = request;
                }
                shared.Join(userId, formats, participant.Note, clock.UtcNow);
                requestId = shared.Id;
            }
            await repository.SaveChangesAsync(token);
            var view = await repository.FindViewAsync(requestId, userId, token);
            return BookRequestCommandResult.Success(view!);
        }, cancellationToken);
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

        var initial = await repository.FindAdminViewAsync(requestId, cancellationToken);
        if (initial is null) return BookRequestCommandResult.NotFound();
        return await repository.InCreateRequestScopeAsync(userId, initial.Request.WorkId, async token =>
        {
            var request = await repository.FindRequestForAdminAsync(requestId, token);
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

            if (request.RequiresManualFulfillment && to == RequestStatus.PendingAcquisition)
                return BookRequestCommandResult.Invalid("This request needs a specific version. It cannot be returned to automatic acquisition.");
            if (to == RequestStatus.PendingAcquisition)
            {
                var active = await repository.GetActiveRequestsForWorkAsync(userId, request.WorkId, token);
                if (active.Any(other => other.Id != requestId && !other.RequiresManualFulfillment))
                    return BookRequestCommandResult.Invalid("An active shared request already exists. Join it from the book page instead.");
            }

            request.TransitionTo(to, userId, reason, clock.UtcNow);
            await repository.SaveChangesAsync(token);
            await audit.WriteAsync(
                AuditActions.BookRequestStatusChanged,
                AuditSubjectTypes.BookRequest,
                requestId.ToString(),
                new { RequestId = requestId, From = request.StatusHistory.Last().FromStatus?.ToString(), To = to.ToString() },
                token);

            var view = await repository.FindAdminViewAsync(requestId, token);

            if (to == RequestStatus.NeedsReview)
            {
                await notifications.RecordRequestNeedsReviewAsync(requestId, view!.Request.WorkTitle, reason, token);
            }
            else if (to is RequestStatus.Available or RequestStatus.NotAvailable)
            {
                foreach (var requesterId in request.ActiveRequesterIds)
                {
                    await notifications.RecordRequestStatusForUserAsync(
                        requesterId, requestId, view!.Request.WorkTitle, to, token);
                    await outboundCommunications.EnqueueAsync(
                        requesterId,
                        OutboundCommunicationTypes.RequestStatusChanged,
                        body: BuildRequestStatusChangedBody(view!.Request.WorkTitle, to),
                        subject: $"Family Librarian — {view!.Request.WorkTitle}",
                        relatedEntityType: "BookRequest",
                        relatedEntityId: requestId,
                        token);
                }
            }

            return BookRequestCommandResult.Success(view!.Request);
        }, cancellationToken);
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

        candidates = candidates.Where(request => !request.RequiresManualFulfillment).ToArray();
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
