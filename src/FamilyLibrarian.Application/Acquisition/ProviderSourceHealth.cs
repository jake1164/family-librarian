using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Decides which source lookup attempts count as a problem with the source
/// itself, as opposed to one book that did not work out or a passing glitch.
/// </summary>
/// <remarks>
/// The ledger is per request, so the most recent attempt for a provider says
/// little about the provider: it may be one book, one dropped connection, or a
/// failure from days ago that no later lookup has overwritten. A configuration
/// block is always actionable and is reported as recorded. An operational
/// failure is reported only once it has repeated, and stops being reported by
/// itself when it is old, so it never needs a manual clear.
/// </remarks>
public static class ProviderSourceHealth
{
    /// <summary>Consecutive failed lookups, with nothing succeeding between them, before a source is flagged.</summary>
    public const int ConsecutiveFailuresToFlag = 3;

    /// <summary>How long after its latest failure a source keeps being flagged without a new attempt.</summary>
    public static readonly TimeSpan OperationalIssueLifetime = TimeSpan.FromHours(24);

    /// <param name="newestFirst">Recent attempts across providers, newest first.</param>
    public static IReadOnlyList<ProviderAttempt> CurrentIssues(
        IReadOnlyList<ProviderAttempt> newestFirst, DateTimeOffset now) =>
        newestFirst
            .GroupBy(attempt => attempt.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => Evaluate(group.ToArray(), now))
            .OfType<ProviderAttempt>()
            .OrderByDescending(attempt => attempt.AttemptedAtUtc)
            .ToArray();

    private static ProviderAttempt? Evaluate(ProviderAttempt[] newestFirst, DateTimeOffset now)
    {
        var latest = newestFirst[0];
        switch (latest.IssueKind)
        {
            case ProviderAttemptIssueKind.Configuration:
                return latest;
            case ProviderAttemptIssueKind.Operational:
                var failures = newestFirst
                    .TakeWhile(attempt => attempt.IssueKind == ProviderAttemptIssueKind.Operational)
                    .Count();
                return failures >= ConsecutiveFailuresToFlag &&
                       now - latest.AttemptedAtUtc <= OperationalIssueLifetime
                    ? latest
                    : null;
            default:
                return null;
        }
    }
}
