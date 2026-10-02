namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// Why one provider result is no longer offered for a request format.
/// </summary>
/// <remarks>
/// The two reasons must stay distinguishable because only one of them costs a
/// download. A requester setting an edition aside is free; an unattended
/// attempt that fetched a file and failed to verify it has already spent a
/// real transfer against a possibly-metered source, and is what
/// <see cref="Providers.ExternalProvider.AutomaticAttemptLimit"/> bounds.
/// Counting a requester's preference against that budget would let someone
/// browsing editions silently exhaust automatic acquisition.
/// </remarks>
public enum DeclinedCandidateReason
{
    /// <summary>The requester chose "keep looking" on a preference review. Costs nothing.</summary>
    RequesterDeclined = 1,

    /// <summary>
    /// Unattended acquisition downloaded this candidate and it failed a
    /// post-download check (provider job failure, security evaluation, or
    /// asset identity). Spent a download; counts against the attempt limit.
    /// </summary>
    AutomaticVerificationFailed = 2
}

/// <summary>
/// Remembers one provider result that is no longer offered for a request
/// format -- either the requester explicitly declined it via "keep looking" on
/// a <see cref="RequestReviewCategory.PreferenceAmbiguity"/> review, so the
/// very next automatic pass does not immediately re-offer the same edition
/// from an otherwise-unchanged catalog, or unattended acquisition already
/// tried it and it failed verification (PROVIDER-7).
/// </summary>
public sealed class DeclinedRequestCandidate
{
    private DeclinedRequestCandidate()
    {
    }

    internal DeclinedRequestCandidate(
        Guid requestId, Guid requestFormatId, string providerId, string providerResultId, DateTimeOffset declinedAtUtc,
        DeclinedCandidateReason reason = DeclinedCandidateReason.RequesterDeclined,
        string? failureReason = null)
    {
        RequestId = requestId;
        RequestFormatId = requestFormatId;
        ProviderId = providerId;
        ProviderResultId = providerResultId;
        DeclinedAtUtc = declinedAtUtc;
        Reason = reason;
        FailureReason = failureReason;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public Guid RequestFormatId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public string ProviderResultId { get; private set; } = string.Empty;

    public DateTimeOffset DeclinedAtUtc { get; private set; }

    public DeclinedCandidateReason Reason { get; private set; } = DeclinedCandidateReason.RequesterDeclined;

    /// <summary>
    /// Why the post-download check rejected this candidate, retained so the
    /// eventual review can say what was already tried instead of only that
    /// something failed. Null for a requester decline.
    /// </summary>
    public string? FailureReason { get; private set; }
}
