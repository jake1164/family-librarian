namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// One user's request for one canonical <c>Work</c>, in one or both media types.
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
            _ => RequestFormatStatus.Requested
        };

        foreach (var format in _formats)
        {
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
