using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// One attempt to obtain a file for one requested format.
/// </summary>
/// <remarks>
/// Manual import (M9) and every future provider-driven acquisition (M10+) share
/// this aggregate: a request can accumulate more than one job across its
/// lifetime (a failed provider attempt, then a manual upload), and jobs must
/// remain queryable independent of the request's own status.
/// </remarks>
public sealed class AcquisitionJob
{
    private readonly List<AcquisitionCandidate> _candidates = [];

    private AcquisitionJob()
    {
    }

    public AcquisitionJob(
        Guid requestId,
        RequestMediaType mediaType,
        string providerId,
        EgressPolicy egressPolicy,
        DateTimeOffset createdAtUtc)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A request ID is required.", nameof(requestId));
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        }

        Id = Guid.NewGuid();
        RequestId = requestId;
        MediaType = mediaType;
        ProviderId = providerId.Trim();
        EgressPolicy = egressPolicy;
        Status = AcquisitionJobStatusTransitions.InitialStatus;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public RequestMediaType MediaType { get; private set; }

    public string ProviderId { get; private set; } = null!;

    public EgressPolicy EgressPolicy { get; private set; }

    public AcquisitionJobStatus Status { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<AcquisitionCandidate> Candidates => _candidates;

    /// <exception cref="InvalidAcquisitionJobTransitionException">
    /// The move is not in <see cref="AcquisitionJobStatusTransitions"/>.
    /// </exception>
    public void TransitionTo(AcquisitionJobStatus to, DateTimeOffset atUtc, string? failureReason = null)
    {
        if (!AcquisitionJobStatusTransitions.IsAllowed(Status, to))
        {
            throw new InvalidAcquisitionJobTransitionException(Status, to);
        }

        StartedAtUtc ??= atUtc;
        Status = to;
        UpdatedAtUtc = atUtc;
        FailureReason = to == AcquisitionJobStatus.Failed
            ? (string.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim())
            : null;

        if (to is AcquisitionJobStatus.CandidateAcquired or AcquisitionJobStatus.Failed
            or AcquisitionJobStatus.Cancelled)
        {
            CompletedAtUtc = atUtc;
        }
    }

    public AcquisitionCandidate AddCandidate(
        string providerId,
        string providerReference,
        string? title,
        string? author,
        string? format,
        long? sizeBytes,
        TimeSpan? duration,
        int? bitrateKbps,
        string? metadataJson,
        double? confidenceScore,
        DateTimeOffset atUtc)
    {
        var candidate = new AcquisitionCandidate(
            Id,
            providerId,
            providerReference,
            title,
            author,
            format,
            sizeBytes,
            duration,
            bitrateKbps,
            metadataJson,
            confidenceScore,
            atUtc);

        _candidates.Add(candidate);
        UpdatedAtUtc = atUtc;
        return candidate;
    }

    /// <exception cref="InvalidOperationException">
    /// <paramref name="candidateId"/> does not belong to this job.
    /// </exception>
    public void MarkCandidateStatus(Guid candidateId, AcquisitionCandidateStatus status, DateTimeOffset atUtc)
    {
        var candidate = _candidates.Find(candidate => candidate.Id == candidateId)
            ?? throw new InvalidOperationException(
                $"Candidate {candidateId} does not belong to acquisition job {Id}.");

        candidate.SetStatus(status, atUtc);
        UpdatedAtUtc = atUtc;
    }
}
