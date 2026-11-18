using System.Globalization;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Budgeted seed/detail reads on the same Hardcover provider instance, credential and handler pipeline as search/detail. Every
/// physical request goes through <see cref="HardcoverGraphQlClient.ExecuteDiscoveryAsync{TData}"/>, so the rate handler reserves
/// each attempt (and the one permitted retry) from the journey budget before it is dispatched.
/// </summary>
/// <remarks>
/// The ordinary search is two requests (ids, then the books); the budgeted one is the same two, both charged. The language-name
/// lookup the ordinary mapping makes is a third, separate request that this path never makes: the candidate's language stays
/// unknown. Nothing here writes or persists.
/// </remarks>
public sealed partial class HardcoverBookMetadataProvider : IBudgetedBookMetadataProvider
{
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

        using var work = budget.BeginWork();
        var search = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<SearchData>(
            httpClient, SearchQuery, new { query = query.Text, page = query.Page, perPage = MaxResultsPerPage }, budget, cancellationToken);
        if (!search.HasData)
        {
            return BudgetStopped<BookCandidateSearchPage>(budget, search.Transport, search.Code);
        }

        var ids = search.Data!.Search?.Ids?.Where(id => id is not null).Select(id => id!.Value).ToArray() ?? [];
        if (search.Errors.Count > 0)
        {
            // A search that errored on some path is unknown, not a verified list of ids.
            return BudgetStopped<BookCandidateSearchPage>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError);
        }

        if (ids.Length == 0)
        {
            return BudgetedRead.Empty<BookCandidateSearchPage>(budget.Usage, time.GetUtcNow());
        }

        var books = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<BooksData>(
            httpClient, BooksByIdsQuery, new { ids }, budget, cancellationToken);
        if (!books.HasData)
        {
            return BudgetStopped<BookCandidateSearchPage>(budget, books.Transport, books.Code);
        }

        var notes = new List<string>();
        var rowErrors = HardcoverRowErrors.Of(books.Errors, "books");
        var rows = books.Data!.Books ?? [];
        var byId = new Dictionary<int, HardcoverBook>();
        for (var index = 0; index < rows.Count; index++)
        {
            if (rowErrors.Failed(index))
            {
                // A row with a failed field is unknown, never a complete record: withheld, and the page says so.
                notes.Add(DiscoveryCodes.PartialFieldError);
                continue;
            }

            byId.TryAdd(rows[index].Id, rows[index]);
        }

        if (rowErrors.Unattributed)
        {
            notes.Add(DiscoveryCodes.PartialFieldError);
        }

        var candidates = new List<BookCandidate>(ids.Length);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var book) && await ToCandidateAsync(book, cancellationToken, resolveLanguage: false) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count < ids.Length)
        {
            notes.Add(RecordsWithheld);
        }

        budget.RecordItems(candidates.Count);
        return BudgetedRead.Found<BookCandidateSearchPage>(
            new BookCandidateSearchPage(candidates, ids.Length == MaxResultsPerPage), budget.Usage, time.GetUtcNow(), Distinct(notes));
    }

    public async Task<BudgetedRead<BookCandidate>> GetDetailsBudgetedAsync(
        string externalId, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return BudgetStopped<BookCandidate>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var response = await HardcoverGraphQlClient.ExecuteDiscoveryAsync<BookByIdData>(
            httpClient, BookByIdQuery, new { id }, budget, cancellationToken);
        if (!response.HasData)
        {
            return BudgetStopped<BookCandidate>(budget, response.Transport, response.Code);
        }

        if (response.Errors.Count > 0)
        {
            return BudgetStopped<BookCandidate>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.PartialFieldError);
        }

        if (response.Data!.Book is not { } book)
        {
            return BudgetedRead.Empty<BookCandidate>(budget.Usage, time.GetUtcNow());
        }

        var candidate = await ToCandidateAsync(book, cancellationToken, resolveLanguage: false);
        budget.RecordItems(candidate is null ? 0 : 1);
        return candidate is null
            ? BudgetedRead.Empty<BookCandidate>(budget.Usage, time.GetUtcNow(), [RecordsWithheld])
            : BudgetedRead.Found<BookCandidate>(candidate, budget.Usage, time.GetUtcNow());
    }

    private const string RecordsWithheld = "records-withheld";

    private BudgetedRead<T> BudgetStopped<T>(IDiscoveryBudget budget, HardcoverTransport transport, string? reason)
        where T : class
    {
        var (outcome, code) = transport switch
        {
            HardcoverTransport.Throttled => (DiscoveryOutcome.Throttled, reason ?? DiscoveryCodes.Throttled),
            HardcoverTransport.TimedOut => (DiscoveryOutcome.TimedOut, DiscoveryCodes.DeadlineReached),
            HardcoverTransport.AuthorizationDenied or HardcoverTransport.SetupMissing => (DiscoveryOutcome.Unavailable, reason ?? DiscoveryCodes.AuthorizationDenied),
            _ => (DiscoveryOutcome.Failed, reason ?? DiscoveryCodes.ProviderError)
        };
        return BudgetStopped<T>(budget, outcome, code);
    }

    private BudgetedRead<T> BudgetStopped<T>(IDiscoveryBudget budget, DiscoveryOutcome outcome, string code)
        where T : class =>
        BudgetedRead.NotAnswered<T>(outcome, budget.Usage, time.GetUtcNow(), [code]);
}
