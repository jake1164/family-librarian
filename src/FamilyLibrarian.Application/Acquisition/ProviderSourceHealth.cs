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
    /// <param name="currentHealth">
    /// The latest health test of each registered external provider, keyed by provider id. A blocked lookup is recorded when a
    /// provider's health check says it cannot search or acquire; once a newer test shows both available, that record describes a
    /// problem that is over, so it is not reported. Providers with no entry are judged on their attempts alone.
    /// </param>
    public static IReadOnlyList<ProviderAttempt> CurrentIssues(
        IReadOnlyList<ProviderAttempt> newestFirst, DateTimeOffset now,
        IReadOnlyDictionary<string, ProviderHealthSnapshot>? currentHealth = null) =>
        newestFirst
            .GroupBy(attempt => attempt.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => Evaluate(group.ToArray(), now, FindHealth(currentHealth, group.Key)))
            .OfType<ProviderAttempt>()
            .OrderByDescending(attempt => attempt.AttemptedAtUtc)
            .ToArray();

    private static ProviderHealthSnapshot? FindHealth(
        IReadOnlyDictionary<string, ProviderHealthSnapshot>? currentHealth, string providerId)
    {
        if (currentHealth is null) return null;
        if (currentHealth.TryGetValue(providerId, out var exact)) return exact;
        foreach (var (id, snapshot) in currentHealth)
        {
            if (string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase)) return snapshot;
        }

        return null;
    }

    private static ProviderAttempt? Evaluate(
        ProviderAttempt[] newestFirst, DateTimeOffset now, ProviderHealthSnapshot? health)
    {
        var latest = newestFirst[0];
        switch (latest.IssueKind)
        {
            case ProviderAttemptIssueKind.Configuration:
                return latest.Outcome == ProviderAttemptOutcome.Blocked && health?.RecoveredSince(latest.AttemptedAtUtc) == true
                    ? null
                    : latest;
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

/// <summary>What the most recent health test of one external provider concluded.</summary>
/// <param name="TestedAtUtc">When that test ran, or null if it never has.</param>
/// <param name="Succeeded">Whether the provider was reachable and answered that test.</param>
/// <param name="SearchStatus">The reported <c>operations.search</c> status, e.g. "Available".</param>
/// <param name="AcquireStatus">The reported <c>operations.acquire</c> status, e.g. "Available".</param>
public sealed record ProviderHealthSnapshot(
    DateTimeOffset? TestedAtUtc, bool? Succeeded, string? SearchStatus, string? AcquireStatus)
{
    /// <summary>
    /// True when a test newer than <paramref name="attemptedAtUtc"/> succeeded and reported both search and acquire
    /// available. Anything weaker (degraded, unavailable, unknown, an older test) leaves the earlier problem standing.
    /// </summary>
    public bool RecoveredSince(DateTimeOffset attemptedAtUtc) =>
        TestedAtUtc is { } testedAtUtc && testedAtUtc > attemptedAtUtc &&
        Succeeded == true &&
        string.Equals(SearchStatus, "Available", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(AcquireStatus, "Available", StringComparison.OrdinalIgnoreCase);
}
