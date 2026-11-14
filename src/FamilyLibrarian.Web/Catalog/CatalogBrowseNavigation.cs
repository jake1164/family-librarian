using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Catalog;

/// <summary>
/// Issues the optional <see cref="CatalogBookNavigationResponse"/> on candidates that search and detail already
/// returned. It reads only candidates the caller already has: it never calls a provider, resolves a candidate or writes
/// to the catalog, and a failure here only omits navigation.
/// </summary>
/// <remarks>
/// Scoped because it uses the scoped <see cref="IClock"/>; the singleton store only ever receives the current time as an
/// argument. A grouped search representative is linked solely through its own provider's stored observation, never
/// through facts merged from another source.
/// </remarks>
public sealed partial class CatalogBrowseNavigation(
    CatalogBrowseContextStore store,
    IClock clock,
    ILogger<CatalogBrowseNavigation> logger)
{
    public static string RunScope(Guid runId) => $"run:{runId:N}";

    public static string DetailScope(string providerId, string externalId) => $"detail:{providerId}\n{externalId}";

    public static string LegacySearchScope(string searchText, int page) => $"legacy:{page}:{searchText}";

    /// <summary>
    /// Attaches navigation to every result of an already-composed search response. <paramref name="outcome"/> supplies
    /// the raw provider results to remember and the candidates, in the same order as <paramref name="response"/>.
    /// </summary>
    public CatalogSearchResponse Attach(
        Guid ownerId, string scopeKey, CatalogSearchOutcome outcome, CatalogSearchResponse response)
    {
        if (outcome.Candidates.Count != response.Results.Count) return response;
        try
        {
            var now = clock.UtcNow;
            var lease = store.Observe(
                ownerId,
                scopeKey,
                outcome.ProviderResults.Select(result => new BrowseObservation(
                    result.ProviderId, result.ProviderName, result.Succeeded, result.Candidates)),
                now);
            var results = response.Results
                .Select((result, index) => result with { Navigation = Build(ownerId, lease, outcome.Candidates[index], now) })
                .ToArray();
            return response with { Results = results };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNavigationFailed(exception);
            return response with { Results = response.Results.Select(result => result with { Navigation = Unavailable() }).ToArray() };
        }
    }

    /// <summary>Navigation for one candidate whose own provider fetch has just completed. No second provider call.</summary>
    public CatalogBookNavigationResponse ForDetail(Guid ownerId, BookCandidate candidate)
    {
        try
        {
            var now = clock.UtcNow;
            var lease = store.Observe(
                ownerId,
                DetailScope(candidate.ProviderId, candidate.ExternalId),
                [new BrowseObservation(candidate.ProviderId, candidate.ProviderName, true, [candidate])],
                now);
            return Build(ownerId, lease, candidate, now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNavigationFailed(exception);
            return Unavailable();
        }
    }

    private CatalogBookNavigationResponse Build(
        Guid ownerId, BrowseContextLease? lease, BookCandidate candidate, DateTimeOffset now)
    {
        if (lease is null) return Unavailable();
        var found = store.TryGetRecord(ownerId, lease.ContextId, candidate.ProviderId, candidate.ExternalId, now);
        if (found is null)
        {
            // The record could not be retained within the bounds; there is nothing to link to.
            return new CatalogBookNavigationResponse(
                lease.ContextId, lease.ExpiresAtUtc, [], [], [CatalogBrowseLimitations.Truncated]);
        }

        var record = found.Record;
        var limitations = new List<string>();
        if (record.CreditsTruncated || record.MembershipsTruncated) limitations.Add(CatalogBrowseLimitations.Truncated);

        // Link only where the label the caller is showing is exactly the label that was observed and stored.
        var authors = record.Credits
            .Where(credit => credit.Ordinal < candidate.Authors.Count &&
                string.Equals(candidate.Authors[credit.Ordinal], credit.Label, StringComparison.Ordinal))
            .Select(credit => new CatalogNavigationReferenceResponse(credit.Ordinal, credit.Label, credit.SubjectId))
            .ToArray();
        var series = record.Memberships
            .Where(membership => membership.Ordinal < candidate.Series.Count &&
                string.Equals(candidate.Series[membership.Ordinal].Name, membership.Label, StringComparison.Ordinal))
            .Select(membership => new CatalogNavigationReferenceResponse(membership.Ordinal, membership.Label, membership.SubjectId))
            .ToArray();

        if (authors.Length < record.Credits.Count || series.Length < record.Memberships.Count) limitations.Add(CatalogBrowseLimitations.NavigationPartial);
        return new CatalogBookNavigationResponse(lease.ContextId, found.ExpiresAtUtc, authors, series, limitations);
    }

    private static CatalogBookNavigationResponse Unavailable() => new(
        null, null, [], [], [CatalogBrowseLimitations.NavigationUnavailable]);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Catalog browse navigation could not be issued; the response is unchanged.")]
    private partial void LogNavigationFailed(Exception exception);
}
