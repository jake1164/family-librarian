namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// A shared household request for one canonical <c>Work</c>, in one or both media types.
/// </summary>
/// <remarks>
/// The request targets a Work, not an Edition: the family asks for a book, and
/// choosing which edition fulfills it is a later administrative decision that
/// must not change what was asked for.
/// </remarks>
public sealed class BookRequest
{
    public const int MaxNoteLength = 1_000;
    public const int MaxAdminNoteLength = 2_000;
    public const int MaxReasonLength = 512;

    private readonly List<RequestFormat> _formats = [];
    private readonly List<RequestStatusHistory> _statusHistory = [];
    private readonly List<RequestParticipant> _participants = [];

    private BookRequest()
    {
    }

    public BookRequest(
        Guid userId,
        Guid workId,
        IEnumerable<RequestMediaType> mediaTypes,
        string? requesterNote,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        if (workId == Guid.Empty)
        {
            throw new ArgumentException("A Work ID is required.", nameof(workId));
        }

        var requestedFormats = mediaTypes.Distinct().ToArray();
        if (requestedFormats.Length == 0)
        {
            throw new ArgumentException(
                "A request must ask for at least one media type.",
                nameof(mediaTypes));
        }

        if (Array.Exists(requestedFormats, mediaType => !Enum.IsDefined(mediaType)))
        {
            throw new ArgumentException("An unknown media type was requested.", nameof(mediaTypes));
        }

        UserId = userId;
        WorkId = workId;
        Status = RequestStatusTransitions.InitialStatus;
        RequesterNote = CleanNote(requesterNote, MaxNoteLength, nameof(requesterNote));
        RequestedAtUtc = createdAtUtc;
        StatusChangedAtUtc = createdAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        _participants.Add(new RequestParticipant(Id, userId, requestedFormats, RequesterNote, createdAtUtc));

        foreach (var mediaType in requestedFormats)
        {
            _formats.Add(new RequestFormat(Id, mediaType, createdAtUtc));
        }

        _statusHistory.Add(new RequestStatusHistory(
            Id,
            null,
            Status,
            userId,
            null,
            createdAtUtc));
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public Guid WorkId { get; private set; }

    public RequestStatus Status { get; private set; }

    public string? RequesterNote { get; private set; }

    public string? AdminNote { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset StatusChangedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<RequestFormat> Formats => _formats;

    public IReadOnlyCollection<RequestStatusHistory> StatusHistory => _statusHistory;

    public IReadOnlyCollection<RequestParticipant> Participants => _participants;

    public string? VersionKind { get; private set; }

    public string? VersionDetails { get; private set; }

    public bool RequiresManualFulfillment { get; private set; }

    public IEnumerable<Guid> ActiveRequesterIds => _participants
        .Where(participant => participant.WithdrawnAtUtc is null).Select(participant => participant.UserId);

    public IEnumerable<Guid> SatisfiedRequesterIds => _participants
        .Where(participant => participant.WithdrawnAtUtc is null &&
            _formats.Where(format => (format.MediaType == RequestMediaType.Ebook && participant.WantsEbook) ||
                                     (format.MediaType == RequestMediaType.Audiobook && participant.WantsAudiobook))
                .All(format => format.Status == RequestFormatStatus.Available))
        .Select(participant => participant.UserId);

    public void RequireVersionReview(string kind, string details, Guid actorUserId, DateTimeOffset atUtc)
    {
        if (kind is not ("Language" or "Edition" or "Narration" or "Accessibility" or "Replacement"))
            throw new ArgumentException("Choose a supported version difference.", nameof(kind));
        VersionDetails = CleanNote(details, MaxNoteLength, nameof(details))
            ?? throw new ArgumentException("Describe the version needed.", nameof(details));
        VersionKind = kind;
        RequiresManualFulfillment = true;
        TransitionTo(RequestStatus.NeedsReview, actorUserId, "A specific version requires librarian review.", atUtc);
    }

    public void Join(Guid userId, IEnumerable<RequestMediaType> mediaTypes, string? note, DateTimeOffset atUtc)
    {
        if (!IsActive)
            throw new InvalidOperationException("Only an active request can be joined.");
        if (userId == Guid.Empty) throw new ArgumentException("A user ID is required.", nameof(userId));
        var formats = mediaTypes.Distinct().ToArray();
        if (formats.Length == 0 || formats.Any(format => !Enum.IsDefined(format)))
            throw new ArgumentException("Choose a requested format.", nameof(mediaTypes));
        var participant = _participants.SingleOrDefault(candidate => candidate.UserId == userId);
        if (participant is null) _participants.Add(new RequestParticipant(Id, userId, formats, note, atUtc));
        else participant.Join(formats, note);
        foreach (var format in formats.Where(format => !RequestsFormat(format)))
            _formats.Add(new RequestFormat(Id, format, atUtc));
        UpdatedAtUtc = atUtc;
    }

    public void Withdraw(Guid userId, DateTimeOffset atUtc)
    {
        var participant = _participants.SingleOrDefault(candidate => candidate.UserId == userId)
            ?? throw new InvalidOperationException("That person is not a requester.");
        participant.Withdraw(atUtc);
        UpdatedAtUtc = atUtc;
        if (IsActive && !ActiveRequesterIds.Any())
            TransitionTo(RequestStatus.Cancelled, userId, "All requesters withdrew their interest.", atUtc);
    }

    public bool IsActive => RequestStatusTransitions.IsActive(Status);

    public bool RequestsFormat(RequestMediaType mediaType) =>
        _formats.Exists(format => format.MediaType == mediaType);

    /// <summary>
    /// Moves the request to <paramref name="to"/>, recording history and
    /// bringing the per-format rows along.
    /// </summary>
    /// <exception cref="InvalidRequestTransitionException">
    /// The move is not in <see cref="RequestStatusTransitions"/>.
    /// </exception>
    public void TransitionTo(
        RequestStatus to,
        Guid? actorUserId,
        string? reason,
        DateTimeOffset atUtc)
    {
        if (RequiresManualFulfillment && to == RequestStatus.PendingAcquisition)
            throw new InvalidOperationException("A specific-version request cannot enter automatic acquisition.");
        if (!RequestStatusTransitions.IsAllowed(Status, to))
        {
            throw new InvalidRequestTransitionException(Status, to);
        }

        var from = Status;
        Status = to;
        StatusChangedAtUtc = atUtc;
        UpdatedAtUtc = atUtc;

        var formatStatus = to switch
        {
            RequestStatus.NotAvailable => RequestFormatStatus.NotAvailable,
            RequestStatus.Cancelled => RequestFormatStatus.Cancelled,
            RequestStatus.Available => RequestFormatStatus.Available,
            _ => RequestFormatStatus.Requested
        };

        foreach (var format in _formats)
        {
            // Returning a request to the acquisition queue must not clobber a
            // format that has already been delivered — only the formats still
            // outstanding go back to "Requested".
            if (formatStatus == RequestFormatStatus.Requested && format.Status == RequestFormatStatus.Available)
            {
                continue;
            }

            format.SetStatus(formatStatus, atUtc);
        }

        _statusHistory.Add(new RequestStatusHistory(
            Id,
            from,
            to,
            actorUserId,
            CleanNote(reason, MaxReasonLength, nameof(reason)),
            atUtc));
    }

    /// <summary>
    /// Records a single requested format as available. The request itself only
    /// completes once every requested format is available.
    /// </summary>
    /// <returns><see langword="true"/> when the whole request completed.</returns>
    public bool MarkFormatAvailable(Guid requestFormatId, DateTimeOffset atUtc)
    {
        if (!IsActive)
        {
            return false;
        }

        var format = _formats.SingleOrDefault(candidate => candidate.Id == requestFormatId);
        if (format is null)
        {
            throw new ArgumentException("The requested format does not belong to this request.", nameof(requestFormatId));
        }

        format.SetStatus(RequestFormatStatus.Available, atUtc);
        UpdatedAtUtc = atUtc;

        if (!_formats.All(candidate => candidate.Status == RequestFormatStatus.Available))
        {
            return false;
        }

        TransitionTo(RequestStatus.Available, actorUserId: null, "Available in the family library.", atUtc);
        return true;
    }

    public void SetAdminNote(string? adminNote, DateTimeOffset atUtc)
    {
        AdminNote = CleanNote(adminNote, MaxAdminNoteLength, nameof(adminNote));
        UpdatedAtUtc = atUtc;
    }

    private static string? CleanNote(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"The text may not exceed {maxLength} characters.",
                parameterName);
        }

        return trimmed;
    }
}
