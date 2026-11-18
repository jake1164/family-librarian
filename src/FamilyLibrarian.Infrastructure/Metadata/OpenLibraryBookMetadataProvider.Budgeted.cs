using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Budgeted seed/detail reads on the same Open Library provider instance and handler pipeline (so the same request gate and
/// cooldown apply). Each logical read is one physical attempt reserved from the journey budget before dispatch, with the gate's
/// pacing counted as provider work, exactly like the discovery reads.
/// </summary>
/// <remarks>
/// The ordinary detail view makes a second request for the Work's edition list; the budgeted detail does not. The candidate then
/// carries the search document's own edition facts and the result says the list was not read, rather than hiding an unbudgeted
/// request. Nothing here writes or persists.
/// </remarks>
public sealed partial class OpenLibraryBookMetadataProvider : IBudgetedBookMetadataProvider
{
    private const string EditionListNotRead = "edition-list-not-read";

    string IBudgetedBookMetadataProvider.ProviderId => Id;

    public async Task<BudgetedRead<BookCandidateSearchPage>> SearchBudgetedAsync(
        BookSearchQuery query, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        query.Validate();
        ArgumentNullException.ThrowIfNull(budget);
        if (!budget.TryAdmitPage())
        {
            return BudgetStopped<BookCandidateSearchPage>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        var providerQuery = IsbnNormalizer.TryNormalizeQuery(query.Text, out var isbn) ? $"isbn:{isbn}" : query.Text;
        using var work = budget.BeginWork();
        var reply = await GetAsync<OpenLibrarySearchResponse>(
            OrdinarySearchUri(providerQuery, SearchFields, _options.MaxResults, query.Page), budget, cancellationToken);
        if (!reply.HasData)
        {
            return BudgetStopped<BookCandidateSearchPage>(budget, reply.Transport, reply.Code);
        }

        var page = ToSearchPage(reply.Data, _options.MaxResults, query.Page);
        budget.RecordItems(page.Candidates.Count);
        return BudgetedRead.Found<BookCandidateSearchPage>(page, budget.Usage, time.GetUtcNow());
    }

    public async Task<BudgetedRead<BookCandidate>> GetDetailsBudgetedAsync(
        string externalId, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!IsValidWorkId(externalId))
        {
            return BudgetStopped<BookCandidate>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<OpenLibrarySearchResponse>(
            OrdinarySearchUri($"key:/works/{externalId}", SearchFields, 1, 1), budget, cancellationToken);
        if (!reply.HasData)
        {
            return BudgetStopped<BookCandidate>(budget, reply.Transport, reply.Code);
        }

        var document = reply.Data!.Documents?.SingleOrDefault(candidate =>
            string.Equals(GetWorkId(candidate.Key), externalId, StringComparison.Ordinal));
        var found = document is null ? null : ToCandidate(document, trustUntaggedEditionTitle: false);
        budget.RecordItems(found is null ? 0 : 1);
        return found is null
            ? BudgetedRead.Empty<BookCandidate>(budget.Usage, time.GetUtcNow())
            : BudgetedRead.Found<BookCandidate>(found, budget.Usage, time.GetUtcNow(), [EditionListNotRead]);
    }

    private BudgetedRead<T> BudgetStopped<T>(IDiscoveryBudget budget, OpenLibraryTransport transport, string? reason)
        where T : class
    {
        var (outcome, code) = transport switch
        {
            OpenLibraryTransport.Throttled => (DiscoveryOutcome.Throttled, reason ?? DiscoveryCodes.Throttled),
            OpenLibraryTransport.TimedOut => (DiscoveryOutcome.TimedOut, DiscoveryCodes.DeadlineReached),
            OpenLibraryTransport.Unavailable => (DiscoveryOutcome.Unavailable, reason ?? DiscoveryCodes.AuthorizationDenied),
            _ => (DiscoveryOutcome.Failed, reason ?? DiscoveryCodes.ProviderError)
        };
        return BudgetStopped<T>(budget, outcome, code);
    }

    private BudgetedRead<T> BudgetStopped<T>(IDiscoveryBudget budget, DiscoveryOutcome outcome, string code)
        where T : class =>
        BudgetedRead.NotAnswered<T>(outcome, budget.Usage, time.GetUtcNow(), [code]);
}
