using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Audit;
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
    NotificationService notifications)
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
        }

        return BookRequestCommandResult.Success(view!.Request);
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
}

public sealed record CreateBookRequestResult(
    CreateBookRequestOutcome Outcome,
    BookRequestView? Request,
    IReadOnlyList<RequestMediaType> OverlappingFormats,
    string? Error)
{
    public static CreateBookRequestResult Created(BookRequestView request) =>
        new(CreateBookRequestOutcome.Created, request, [], null);

    public static CreateBookRequestResult Duplicate(
        BookRequestView? existing,
        IReadOnlyList<RequestMediaType> overlappingFormats) =>
        new(CreateBookRequestOutcome.DuplicateWarning, existing, overlappingFormats, null);

    public static CreateBookRequestResult WorkNotFound() =>
        new(CreateBookRequestOutcome.WorkNotFound, null, [], null);

    public static CreateBookRequestResult Invalid(string error) =>
        new(CreateBookRequestOutcome.Invalid, null, [], error);

    public static CreateBookRequestResult Unauthenticated() =>
        new(CreateBookRequestOutcome.Unauthenticated, null, [], null);
}

public enum CreateBookRequestOutcome
{
    Created,
    DuplicateWarning,
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
