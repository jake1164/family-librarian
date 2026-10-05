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
    private readonly List<RequestReviewCandidate> _reviewCandidates = [];
    private readonly List<DeclinedRequestCandidate> _declinedCandidates = [];

    private BookRequest()
    {
    }

    public BookRequest(
        Guid userId,
        Guid workId,
        IEnumerable<RequestMediaType> mediaTypes,
        string? requesterNote,
        DateTimeOffset createdAtUtc,
        Guid? deliveryTargetId = null)
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
        _participants.Add(new RequestParticipant(Id, userId, requestedFormats, RequesterNote, createdAtUtc, deliveryTargetId));

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

    /// <summary>Why this request is <see cref="RequestStatus.NeedsReview"/> -- null otherwise. See SELFSERV-1.</summary>
    public RequestReviewCategory? ReviewCategory { get; private set; }

    /// <summary>Populated only for <see cref="RequestReviewCategory.PreferenceAmbiguity"/>.</summary>
    public IReadOnlyCollection<RequestReviewCandidate> ReviewCandidates => _reviewCandidates;

    /// <summary>Provider results previously declined via "keep looking" -- see <see cref="DeclinedRequestCandidate"/>.</summary>
    public IReadOnlyCollection<DeclinedRequestCandidate> DeclinedCandidates => _declinedCandidates;

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

    /// <summary>
    /// Moves an automatically-processed request to <see cref="RequestStatus.NeedsReview"/>
    /// for one of the three <see cref="RequestReviewCategory"/> reasons (SELFSERV-1:
    /// routing preference ambiguity to the requesting user, not just admins).
    /// Only <see cref="RequestReviewCategory.PreferenceAmbiguity"/> carries
    /// candidates -- the other two categories keep today's admin-only behavior
    /// unchanged and have nothing for a requester to pick between.
    /// </summary>
    public void MarkNeedsReview(
        RequestReviewCategory category,
        string reason,
        DateTimeOffset atUtc,
        IReadOnlyList<RequestReviewCandidateInput>? candidates = null)
    {
        if (category == RequestReviewCategory.PreferenceAmbiguity)
        {
            if (candidates is null || candidates.Count == 0)
            {
                throw new ArgumentException(
                    "A preference-ambiguity review requires at least one candidate.", nameof(candidates));
            }
        }
        else if (candidates is { Count: > 0 })
        {
            throw new ArgumentException(
                "Only a preference-ambiguity review carries candidates.", nameof(candidates));
        }

        _reviewCandidates.Clear();
        if (candidates is not null)
        {
            // Retain every distinct provider record for the administrator.
            // Requester-facing UI deliberately withholds a multi-record choice,
            // but collapsing here would leave the librarian with a blind approval
            // while the review reason still truthfully says there were several.
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                _reviewCandidates.Add(new RequestReviewCandidate(
                    Id, candidate.RequestFormatId, candidate.ProviderId, candidate.ProviderResultId, candidate.Title,
                    candidate.Author, candidate.Language, candidate.Details, candidate.AdminInspectionUri,
                    candidate.ReleaseName, candidate.TitleIsRequestFallback, index, atUtc));
            }
        }

        ReviewCategory = category;
        TransitionTo(RequestStatus.NeedsReview, actorUserId: null, reason, atUtc);
    }

    /// <summary>
    /// Replaces stale evidence for an existing preference review without
    /// reopening the request. Used only when a background worker can recover
    /// evidence that an older review did not retain.
    /// </summary>
    public void RefreshPreferenceReview(
        string reason,
        DateTimeOffset atUtc,
        IReadOnlyList<RequestReviewCandidateInput> candidates)
    {
        if (Status != RequestStatus.NeedsReview || ReviewCategory != RequestReviewCategory.PreferenceAmbiguity)
        {
            throw new InvalidOperationException("Only an existing preference review can be refreshed.");
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("A preference review requires at least one candidate.", nameof(candidates));
        }

        _reviewCandidates.Clear();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            _reviewCandidates.Add(new RequestReviewCandidate(
                Id, candidate.RequestFormatId, candidate.ProviderId, candidate.ProviderResultId, candidate.Title,
                candidate.Author, candidate.Language, candidate.Details, candidate.AdminInspectionUri,
                candidate.ReleaseName, candidate.TitleIsRequestFallback, index, atUtc));
        }

        StatusChangedAtUtc = atUtc;
        UpdatedAtUtc = atUtc;
        _statusHistory.Add(new RequestStatusHistory(
            Id, RequestStatus.NeedsReview, RequestStatus.NeedsReview, actorUserId: null,
            CleanNote(reason, MaxReasonLength, nameof(reason)), atUtc));
    }

    /// <summary>
    /// Adds review candidates for a format not yet represented in an existing
    /// preference-ambiguity review, leaving candidates already stored for any
    /// other format untouched.
    /// </summary>
    /// <remarks>
    /// Two different formats of the same request can each complete their own
    /// automatic lookup in the same background pass. The first to find
    /// candidates calls <see cref="MarkNeedsReview"/> and flips the status;
    /// without this, the second format's own candidates would then either be
    /// silently dropped (a caller that only checks for
    /// <see cref="RequestStatus.PendingAcquisition"/> before recording
    /// anything) or wipe the first format's candidates entirely (a caller
    /// that instead used <see cref="RefreshPreferenceReview"/>, which
    /// replaces the whole list rather than appending to it). Observed live:
    /// an external provider's attempt log said "Found 1 candidate(s); choose
    /// a reviewed candidate" for a candidate that was never actually stored
    /// anywhere, because a different format's provider had claimed the
    /// transition a second earlier in the same pass.
    /// </remarks>
    public void AddReviewCandidatesForFormat(
        Guid requestFormatId,
        string reason,
        DateTimeOffset atUtc,
        IReadOnlyList<RequestReviewCandidateInput> candidates)
    {
        if (Status != RequestStatus.NeedsReview || ReviewCategory != RequestReviewCategory.PreferenceAmbiguity)
        {
            throw new InvalidOperationException(
                "Only an existing preference-ambiguity review can have a format's candidates added to it.");
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        if (candidates.Any(candidate => candidate.RequestFormatId != requestFormatId))
        {
            throw new ArgumentException(
                "Every candidate must belong to the format being added.", nameof(candidates));
        }

        if (_reviewCandidates.Any(candidate => candidate.RequestFormatId == requestFormatId))
        {
            // Nothing to do: a previous pass already recorded this format's
            // candidates (e.g. a retry landed here after success).
            return;
        }

        var nextDisplayOrder = _reviewCandidates.Count == 0
            ? 0
            : _reviewCandidates.Max(candidate => candidate.DisplayOrder) + 1;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            _reviewCandidates.Add(new RequestReviewCandidate(
                Id, candidate.RequestFormatId, candidate.ProviderId, candidate.ProviderResultId, candidate.Title,
                candidate.Author, candidate.Language, candidate.Details, candidate.AdminInspectionUri,
                candidate.ReleaseName, candidate.TitleIsRequestFallback, nextDisplayOrder + index, atUtc));
        }

        UpdatedAtUtc = atUtc;
        _statusHistory.Add(new RequestStatusHistory(
            Id, RequestStatus.NeedsReview, RequestStatus.NeedsReview, actorUserId: null,
            CleanNote(reason, MaxReasonLength, nameof(reason)), atUtc));
    }

    /// <summary>
    /// The requester (or an admin -- additive, not exclusive) accepts a
    /// specific <see cref="RequestReviewCandidate"/> from a
    /// <see cref="RequestReviewCategory.PreferenceAmbiguity"/> review ("get it
    /// anyway"). Returns the accepted candidate so the caller can drive
    /// acquisition of that specific provider result; this method only clears
    /// review state and reopens the request for automatic processing.
    /// </summary>
    public RequestReviewCandidate AcceptReviewCandidate(Guid candidateId, Guid? actorUserId, DateTimeOffset atUtc)
    {
        if (Status != RequestStatus.NeedsReview || ReviewCategory != RequestReviewCategory.PreferenceAmbiguity)
        {
            throw new InvalidOperationException("Only a preference-ambiguity review has a candidate to accept.");
        }

        var candidate = _reviewCandidates.SingleOrDefault(existing => existing.Id == candidateId)
            ?? throw new ArgumentException("That candidate does not belong to this request's review.", nameof(candidateId));

        var format = _formats.Single(candidateFormat => candidateFormat.Id == candidate.RequestFormatId);
        format.AcceptLanguage(candidate.Language);

        ReviewCategory = null;
        _reviewCandidates.Clear();
        TransitionTo(RequestStatus.PendingAcquisition, actorUserId, "The requester accepted a lower-confidence match.", atUtc);
        return candidate;
    }

    /// <summary>
    /// The requester (or an admin) declines every offered candidate ("keep
    /// looking"). Returns the request to automatic acquisition unchanged --
    /// the existing per-provider retry cooldown means it will not be
    /// re-offered on the very next poll.
    /// </summary>
    public void DismissReviewPreference(Guid? actorUserId, DateTimeOffset atUtc)
    {
        if (Status != RequestStatus.NeedsReview || ReviewCategory != RequestReviewCategory.PreferenceAmbiguity)
        {
            throw new InvalidOperationException("Only a preference-ambiguity review can be dismissed this way.");
        }

        foreach (var candidate in _reviewCandidates)
        {
            _declinedCandidates.RemoveAll(declined =>
                declined.RequestFormatId == candidate.RequestFormatId &&
                declined.ProviderId == candidate.ProviderId &&
                declined.ProviderResultId == candidate.ProviderResultId);
            _declinedCandidates.Add(new DeclinedRequestCandidate(
                Id, candidate.RequestFormatId, candidate.ProviderId, candidate.ProviderResultId, atUtc));
        }

        ReviewCategory = null;
        _reviewCandidates.Clear();
        TransitionTo(RequestStatus.PendingAcquisition, actorUserId, "The requester chose to keep looking for a better match.", atUtc);
    }

    /// <summary>
    /// Records that unattended acquisition fetched one candidate and it failed
    /// a post-download check, so the next automatic pass advances to the next
    /// ranked candidate instead of re-downloading the same bad file
    /// (PROVIDER-7).
    /// </summary>
    /// <remarks>
    /// Deliberately does not change <see cref="Status"/>. A single failed
    /// candidate is not a reason to stop: the caller decides whether the
    /// provider's attempt budget is now spent and only then moves the request
    /// to review. Recorded with
    /// <see cref="DeclinedCandidateReason.AutomaticVerificationFailed"/> so it
    /// is countable against that budget and distinguishable from a requester's
    /// free "keep looking".
    /// </remarks>
    public void RecordAutomaticCandidateFailure(
        Guid requestFormatId, string providerId, string providerResultId, string? failureReason, DateTimeOffset atUtc,
        string? releaseFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerResultId);

        _declinedCandidates.RemoveAll(declined =>
            declined.RequestFormatId == requestFormatId &&
            declined.ProviderId == providerId &&
            declined.ProviderResultId == providerResultId);
        _declinedCandidates.Add(new DeclinedRequestCandidate(
            Id, requestFormatId, providerId, providerResultId, atUtc,
            DeclinedCandidateReason.AutomaticVerificationFailed, failureReason, releaseFingerprint));
    }

    /// <summary>
    /// How many distinct candidates unattended acquisition has already
    /// downloaded and failed to verify for one format from one provider.
    /// </summary>
    public int CountAutomaticCandidateFailures(Guid requestFormatId, string providerId) =>
        _declinedCandidates.Count(declined =>
            declined.RequestFormatId == requestFormatId &&
            declined.Reason == DeclinedCandidateReason.AutomaticVerificationFailed &&
            string.Equals(declined.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    public void Join(
        Guid userId,
        IEnumerable<RequestMediaType> mediaTypes,
        string? note,
        DateTimeOffset atUtc,
        Guid? deliveryTargetId = null)
    {
        if (!IsActive)
            throw new InvalidOperationException("Only an active request can be joined.");
        if (userId == Guid.Empty) throw new ArgumentException("A user ID is required.", nameof(userId));
        var formats = mediaTypes.Distinct().ToArray();
        if (formats.Length == 0 || formats.Any(format => !Enum.IsDefined(format)))
            throw new ArgumentException("Choose a requested format.", nameof(mediaTypes));
        var participant = _participants.SingleOrDefault(candidate => candidate.UserId == userId);
        if (participant is null) _participants.Add(new RequestParticipant(Id, userId, formats, note, atUtc, deliveryTargetId));
        else participant.Join(formats, note, deliveryTargetId);
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

        // A NeedsReview review (and any offered candidates) is only ever
        // meaningful while the request stays NeedsReview. Any other
        // transition out of it -- an admin override, or Withdraw's own
        // auto-cancel when the last requester leaves -- must invalidate it,
        // so a stale AcceptReviewCandidate/DismissReviewPreference call
        // afterward has nothing left to act on: a cancelled/unavailable
        // request must not be reopened by resolving a review that no
        // longer applies.
        if (from == RequestStatus.NeedsReview && to != RequestStatus.NeedsReview)
        {
            ReviewCategory = null;
            _reviewCandidates.Clear();
        }

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
