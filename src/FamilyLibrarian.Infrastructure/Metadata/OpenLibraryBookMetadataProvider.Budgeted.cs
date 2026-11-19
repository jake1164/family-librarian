using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

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
public sealed partial class OpenLibraryBookMetadataProvider : IBudgetedBookMetadataProvider, IWorkEditionTitlesProvider
{
    private const string EditionListNotRead = "edition-list-not-read";

    /// <summary>The first page of editions only: one request, never a walk through every edition of a Work.</summary>
    public const int MaxEditionTitles = 20;

    string IBudgetedBookMetadataProvider.ProviderId => Id;

    string IWorkEditionTitlesProvider.ProviderId => Id;

    /// <summary>
    /// One request, no retry: the editions list for one Work, admitted from the journey budget as a single attempt, and cut to the
    /// first <see cref="MaxEditionTitles"/> entries whether or not the source lists more. The reported title, edition key and stated
    /// language are kept exactly; a missing language stays unknown.
    /// </summary>
    public async Task<BudgetedRead<WorkEditionTitlePage>> GetWorkEditionTitlesAsync(
        SourceRef work, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(budget);
        if (work.Kind != ExternalReferenceEntityType.Work || !IsValidWorkId(work.ExternalId))
        {
            return BudgetStopped<WorkEditionTitlePage>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.SubjectInvalid);
        }

        if (!budget.TryAdmitPage())
        {
            return BudgetStopped<WorkEditionTitlePage>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.PageCeiling);
        }

        using var scope = budget.BeginWork();
        var reply = await GetAsync<OpenLibraryEditionsListResponse>(
            $"works/{work.ExternalId}/editions.json?limit={MaxEditionTitles}", budget, cancellationToken);
        if (!reply.HasData)
        {
            return BudgetStopped<WorkEditionTitlePage>(budget, reply.Transport, reply.Code);
        }

        var entries = reply.Data!.Entries ?? [];
        var titles = entries
            .Take(MaxEditionTitles)
            .Select(entry => new WorkEditionTitle(
                EditionIdOf(entry.Key),
                entry.Title?.Trim() ?? string.Empty,
                entry.Languages is { Count: > 0 } languages ? LanguageCodeNormalizer.Normalize(GetLanguageCode(languages[0].Key)) : null,
                "title"))
            .Where(edition => edition.Title.Length > 0)
            .ToArray();
        budget.RecordItems(titles.Length);
        return BudgetedRead.Found<WorkEditionTitlePage>(
            new WorkEditionTitlePage(titles, entries.Count > MaxEditionTitles ? entries.Count : null),
            budget.Usage, time.GetUtcNow());
    }

    /// <summary>The edition's own key without its path prefix; an id is never invented for an entry the source left unnamed.</summary>
    private static string? EditionIdOf(string? key)
    {
        const string prefix = "/books/";
        if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = key[prefix.Length..];
        return id.Length is > 0 and <= SourceRef.MaxExternalIdLength ? id : null;
    }

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
