namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// An immutable record of one provider lookup made for a requested format.
/// This is deliberately separate from an acquisition job: a lookup may find
/// nothing, be blocked by egress policy, or surface candidates for review
/// without ever downloading a file.
/// </summary>
public sealed class ProviderAttempt
{
    private ProviderAttempt()
    {
    }

    public ProviderAttempt(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        ProviderAttemptOutcome outcome,
        string summary,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset? nextEligibleCheckAtUtc)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("A summary is required.", nameof(summary));
        }

        RequestId = requestId;
        RequestFormatId = requestFormatId;
        ProviderId = providerId.Trim().ToLowerInvariant();
        Outcome = outcome;
        Summary = summary.Length <= 512 ? summary : summary[..512];
        AttemptedAtUtc = attemptedAtUtc;
        NextEligibleCheckAtUtc = nextEligibleCheckAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public Guid RequestFormatId { get; private set; }

    public string ProviderId { get; private set; } = null!;

    public ProviderAttemptOutcome Outcome { get; private set; }

    public string Summary { get; private set; } = null!;

    public DateTimeOffset AttemptedAtUtc { get; private set; }

    /// <summary>
    /// Null means this provider will not automatically recheck this format.
    /// </summary>
    public DateTimeOffset? NextEligibleCheckAtUtc { get; private set; }
}

public enum ProviderAttemptOutcome
{
    NoMatch,
    CandidatesFound,
    Acquired,
    Failed,
    Blocked
}
