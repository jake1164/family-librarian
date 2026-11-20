namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// One edition title exactly as a source reported it. It is an edition-title fact about a Work: not a canonical alias, not identity
/// proof, and not a translation or equivalence link. A missing language stays missing.
/// </summary>
/// <param name="EditionId">The source's own edition id, when it stated one.</param>
/// <param name="Title">The title the source reported for that edition; empty never stands in for an unknown title.</param>
/// <param name="Language">The language the source stated for that edition; null when it stated none.</param>
/// <param name="SourceField">The source field the title was read from (a stable code such as <c>title</c>), so a title is attributable.</param>
public sealed record WorkEditionTitle(string? EditionId, string Title, string? Language, string SourceField);

/// <summary>One source's answer for a Work's editions. <see cref="TotalReported"/> is what the source said it had, when it said so.</summary>
public sealed record WorkEditionTitlePage(IReadOnlyList<WorkEditionTitle> Editions, int? TotalReported);

/// <summary>
/// Optional, read-only edition-title expansion an integration may add beside its ordinary and discovery reads, on the same
/// registered provider instance. A source that does not implement it has not implemented the expansion; that is not proof the
/// upstream API cannot.
/// </summary>
/// <remarks>
/// One request and no retry: the read is a single physical attempt admitted from <see cref="IDiscoveryBudget"/>, never a retried
/// call, and never one request per edition. It makes no write, persists nothing, and cancels as
/// <see cref="OperationCanceledException"/> rather than reporting an empty page.
/// </remarks>
public interface IWorkEditionTitlesProvider
{
    string ProviderId { get; }

    /// <summary>The edition titles this source lists for one native Work, bounded to the first page of editions.</summary>
    Task<BudgetedRead<WorkEditionTitlePage>> GetWorkEditionTitlesAsync(
        SourceRef work, IDiscoveryBudget budget, CancellationToken cancellationToken);
}
