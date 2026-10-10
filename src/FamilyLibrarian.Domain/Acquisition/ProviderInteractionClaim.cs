namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// Who currently owns the right to drive one <see cref="ProviderAcquisitionJob"/>'s
/// human-interaction session — held in its own table, keyed by job id, deliberately
/// separate from the job row (see <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c>
/// §2): <see cref="ProviderAcquisitionJob.Version"/> is mapped to Postgres <c>xmin</c>
/// and rewritten every poll, so writing claim state onto that row would make the
/// poller's own saves fail with spurious concurrency exceptions. Claiming is an
/// INSERT with <see cref="JobId"/> as the primary key: two concurrent claims race on
/// a unique-violation, and the loser is the atomicity mechanism, not application logic.
/// </summary>
public sealed class ProviderInteractionClaim
{
    private ProviderInteractionClaim()
    {
    }

    public ProviderInteractionClaim(
        Guid jobId,
        Guid externalProviderId,
        Guid claimedByUserId,
        ProviderInteractionClaimChannel channel,
        Guid? alertId,
        DateTimeOffset claimedAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A job ID is required.", nameof(jobId));
        }

        if (externalProviderId == Guid.Empty)
        {
            throw new ArgumentException("An external provider ID is required.", nameof(externalProviderId));
        }

        if (claimedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A claiming user is required.", nameof(claimedByUserId));
        }

        JobId = jobId;
        ExternalProviderId = externalProviderId;
        ClaimedByUserId = claimedByUserId;
        Channel = channel;
        AlertId = alertId;
        ClaimedAtUtc = claimedAtUtc;
    }

    /// <summary>The <see cref="ProviderAcquisitionJob.Id"/> this claim holds. Also the primary key.</summary>
    public Guid JobId { get; private set; }

    public Guid ExternalProviderId { get; private set; }

    public Guid ClaimedByUserId { get; private set; }

    public ProviderInteractionClaimChannel Channel { get; private set; }

    /// <summary>The alert this claim was made through, when <see cref="Channel"/> is <see cref="ProviderInteractionClaimChannel.MatrixLink"/>.</summary>
    public Guid? AlertId { get; private set; }

    public DateTimeOffset ClaimedAtUtc { get; private set; }

    /// <summary>Updated by the remote-view broker at connect/disconnect — see <c>ProviderRemoteViewBrokerService</c>.</summary>
    public DateTimeOffset? LastViewerActivityAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void RecordViewerActivity(DateTimeOffset atUtc) => LastViewerActivityAtUtc = atUtc;

    /// <summary>
    /// Whether this claim still holds its lease: an actively connected viewer keeps
    /// it alive indefinitely; otherwise it lapses <paramref name="lease"/> after the
    /// later of the claim and the last recorded viewer activity.
    /// </summary>
    public bool IsLeaseLive(DateTimeOffset nowUtc, TimeSpan lease, bool hasActiveViewer)
    {
        if (hasActiveViewer)
        {
            return true;
        }

        var lastActivity = LastViewerActivityAtUtc is { } activity && activity > ClaimedAtUtc
            ? activity
            : ClaimedAtUtc;
        return nowUtc - lastActivity < lease;
    }
}

/// <summary>How a <see cref="ProviderInteractionClaim"/> was established.</summary>
public enum ProviderInteractionClaimChannel
{
    /// <summary>The claimant tapped a Matrix magic link (HUMAN-ACQ-1 D1/D2).</summary>
    MatrixLink,

    /// <summary>The claimant clicked "Start"/"Open remote view" in the authenticated admin UI (D7).</summary>
    InApp,

    /// <summary>An administrator explicitly took over another admin's claim.</summary>
    TakeOver
}
