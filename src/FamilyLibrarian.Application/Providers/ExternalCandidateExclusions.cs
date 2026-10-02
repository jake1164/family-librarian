using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Candidates that must not be nominated again for one request format from one
/// provider: those already set aside, and any record that is the same release
/// as one that failed.
/// </summary>
/// <remarks>
/// Built in one place and handed both to the search that nominates a candidate
/// and to the fetch path that re-derives it. They must agree, or the fetch
/// reaches a different conclusion than the decision it is carrying out.
/// </remarks>
public sealed record ExternalCandidateExclusions(
    IReadOnlySet<string> ResultIds,
    IReadOnlySet<string> FailedReleaseFingerprints)
{
    public static readonly ExternalCandidateExclusions None = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <param name="exceptResultId">
    /// A candidate being fetched right now is never excluded by its own earlier
    /// entry, so re-deriving it for the fetch does not remove it.
    /// </param>
    public static ExternalCandidateExclusions From(
        IEnumerable<DeclinedRequestCandidate> declines, string providerId, Guid requestFormatId,
        string? exceptResultId = null)
    {
        var relevant = declines
            .Where(declined => declined.RequestFormatId == requestFormatId &&
                string.Equals(declined.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new ExternalCandidateExclusions(
            relevant
                .Select(declined => declined.ProviderResultId)
                .Where(id => !string.Equals(id, exceptResultId, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal),
            relevant
                .Where(declined => declined.Reason == DeclinedCandidateReason.AutomaticVerificationFailed)
                .Select(declined => declined.ReleaseFingerprint)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal));
    }

    public bool Excludes(Catalog.FulfillmentOption option) =>
        ResultIds.Contains(option.ProviderResultId) ||
        (ExternalReleaseFingerprint.Compute(option.ReleaseName, option.SizeBytes) is { } fingerprint &&
         FailedReleaseFingerprints.Contains(fingerprint));
}
