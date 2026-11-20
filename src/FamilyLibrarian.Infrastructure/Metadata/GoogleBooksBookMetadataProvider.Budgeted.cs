using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Budgeted seed/detail reads on the same Google Books provider instance and credential pipeline. Every physical attempt,
/// including the one permitted retry, is reserved from the journey budget by <see cref="GoogleBooksTransientRetryHandler"/>
/// before it is dispatched; the handler's own two-retry policy does not apply to a request that carries a budget. The API key
/// is added by the existing handler and never appears in a result.
/// </summary>
/// <remarks>Nothing here writes or persists. A Google volume is an edition, so the candidate's source kind and date basis are
/// exactly what the ordinary mapping gives.</remarks>
public sealed partial class GoogleBooksBookMetadataProvider : IBudgetedBookMetadataProvider
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
        var reply = await GetAsync<GoogleBooksVolumesResponse>(SearchUri(query), budget, cancellationToken);
        if (!reply.HasData)
        {
            return BudgetStopped<BookCandidateSearchPage>(budget, reply.Transport, reply.Code);
        }

        var page = ToSearchPage(reply.Data, query.Page);
        budget.RecordItems(page.Candidates.Count);
        return BudgetedRead.Found<BookCandidateSearchPage>(page, budget.Usage, time.GetUtcNow());
    }

    public async Task<BudgetedRead<BookCandidate>> GetDetailsBudgetedAsync(
        string externalId, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!IsValidExternalId(externalId))
        {
            return BudgetStopped<BookCandidate>(budget, DiscoveryOutcome.Failed, DiscoveryCodes.SubjectInvalid);
        }

        using var work = budget.BeginWork();
        var reply = await GetAsync<GoogleBooksVolume>(
            $"volumes/{Uri.EscapeDataString(externalId)}?projection=full", budget, cancellationToken);
        if (reply.Transport == GoogleTransport.NotFound)
        {
            // The volume read succeeded as a 404: a verified "no such volume" at this source, not a failure.
            return BudgetedRead.Empty<BookCandidate>(budget.Usage, time.GetUtcNow());
        }

        if (!reply.HasData)
        {
            return BudgetStopped<BookCandidate>(budget, reply.Transport, reply.Code);
        }

        var found = ToCandidate(reply.Data!);
        budget.RecordItems(found is null ? 0 : 1);
        return found is null
            ? BudgetedRead.Empty<BookCandidate>(budget.Usage, time.GetUtcNow(), ["records-withheld"])
            : BudgetedRead.Found<BookCandidate>(found, budget.Usage, time.GetUtcNow());
    }

    private BudgetedRead<T> BudgetStopped<T>(IDiscoveryBudget budget, GoogleTransport transport, string? reason)
        where T : class
    {
        var (outcome, code) = transport switch
        {
            GoogleTransport.Throttled => (DiscoveryOutcome.Throttled, reason ?? DiscoveryCodes.Throttled),
            GoogleTransport.TimedOut => (DiscoveryOutcome.TimedOut, DiscoveryCodes.DeadlineReached),
            GoogleTransport.Unavailable => (DiscoveryOutcome.Unavailable, reason ?? DiscoveryCodes.AuthorizationDenied),
            GoogleTransport.SetupMissing => (DiscoveryOutcome.Unavailable, DiscoveryCodes.SetupMissing),
            _ => (DiscoveryOutcome.Failed, reason ?? DiscoveryCodes.ProviderError)
        };
        return BudgetStopped<T>(budget, outcome, code);
    }

    private BudgetedRead<T> BudgetStopped<T>(IDiscoveryBudget budget, DiscoveryOutcome outcome, string code)
        where T : class =>
        BudgetedRead.NotAnswered<T>(outcome, budget.Usage, time.GetUtcNow(), [code]);
}
