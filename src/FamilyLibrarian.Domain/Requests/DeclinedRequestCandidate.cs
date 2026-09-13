namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// Remembers one provider result the requester explicitly declined via
/// "keep looking" on a <see cref="RequestReviewCategory.PreferenceAmbiguity"/>
/// review, so the very next automatic pass does not immediately re-offer the
/// same edition from an otherwise-unchanged catalog.
/// </summary>
public sealed class DeclinedRequestCandidate
{
    private DeclinedRequestCandidate()
    {
    }

    internal DeclinedRequestCandidate(
        Guid requestId, Guid requestFormatId, string providerId, string providerResultId, DateTimeOffset declinedAtUtc)
    {
        RequestId = requestId;
        RequestFormatId = requestFormatId;
        ProviderId = providerId;
        ProviderResultId = providerResultId;
        DeclinedAtUtc = declinedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public Guid RequestFormatId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public string ProviderResultId { get; private set; } = string.Empty;

    public DateTimeOffset DeclinedAtUtc { get; private set; }
}
