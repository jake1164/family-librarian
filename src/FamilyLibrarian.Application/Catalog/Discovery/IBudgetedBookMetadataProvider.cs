namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// One ordinary search/detail read made under a journey's <see cref="IDiscoveryBudget"/>. It carries only what an answer needs
/// to be truthful: the outcome, the value when there is one, stable reason codes and the actual use. Provider text, URLs, keys
/// and headers never travel in it.
/// </summary>
/// <remarks>
/// <see cref="DiscoveryOutcome.Ok"/> holds a value; <see cref="DiscoveryOutcome.Empty"/> says the read succeeded and held nothing
/// (never global absence). Every other outcome is a failure to answer and is never read as empty.
/// </remarks>
public sealed class BudgetedRead<T>
    where T : class
{
    internal BudgetedRead(DiscoveryOutcome outcome, T? value, IReadOnlyList<string> notes, DiscoveryUsage usage, DateTimeOffset observedAtUtc)
    {
        var kept = DiscoverySnapshots.Of(notes);
        foreach (var note in kept)
        {
            DiscoveryCodes.ThrowIfNotCode(note);
        }

        Outcome = outcome;
        Value = value;
        Notes = kept;
        Usage = usage;
        ObservedAtUtc = observedAtUtc;
    }

    public DiscoveryOutcome Outcome { get; }

    public T? Value { get; }

    public IReadOnlyList<string> Notes { get; }

    public DiscoveryUsage Usage { get; }

    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Factories for <see cref="BudgetedRead{T}"/>; a non-generic home so callers infer or name the value type once.</summary>
public static class BudgetedRead
{
    public static BudgetedRead<T> Found<T>(T value, DiscoveryUsage usage, DateTimeOffset observedAtUtc, IReadOnlyList<string>? notes = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(DiscoveryOutcome.Ok, value, notes ?? [], usage, observedAtUtc);
    }

    /// <summary>The read succeeded and the source held nothing for it.</summary>
    public static BudgetedRead<T> Empty<T>(DiscoveryUsage usage, DateTimeOffset observedAtUtc, IReadOnlyList<string>? notes = null)
        where T : class =>
        new(DiscoveryOutcome.Empty, null, notes ?? [], usage, observedAtUtc);

    /// <summary>Unavailable, failed, timed out, throttled, unsupported or unverified: nothing was answered, and a reason code says why.</summary>
    public static BudgetedRead<T> NotAnswered<T>(
        DiscoveryOutcome outcome, DiscoveryUsage usage, DateTimeOffset observedAtUtc, IReadOnlyList<string> notes)
        where T : class
    {
        if (outcome is DiscoveryOutcome.Ok or DiscoveryOutcome.Empty)
        {
            throw new ArgumentException("This outcome carries an answer; use its own factory.", nameof(outcome));
        }

        if (notes.Count == 0)
        {
            throw new ArgumentException("An unanswered read needs a reason code.", nameof(notes));
        }

        return new(outcome, null, notes, usage, observedAtUtc);
    }
}

/// <summary>
/// Optional budgeted seed/detail reads that an integration adds beside <see cref="IBookMetadataProvider"/> on the same registered
/// instance. The ordinary <c>SearchAsync</c>/<c>GetDetailsAsync</c> signatures have no budget and are unchanged; this contract
/// exists so a chat or browse answer that must find an anchor first can count that read against the same journey allowance and
/// deadline as the discovery reads that follow it.
/// </summary>
/// <remarks>
/// Implementations reserve every physical attempt, retry and ancillary request from <see cref="IDiscoveryBudget"/> before it is
/// dispatched, or do not make the ancillary request at all (a language-name lookup, an edition-list read): an unbudgeted fallback
/// is not permitted. A caller's cancellation surfaces as <see cref="OperationCanceledException"/>; it is never an empty result.
/// They write nothing and persist nothing.
/// </remarks>
public interface IBudgetedBookMetadataProvider
{
    string ProviderId { get; }

    /// <summary>One page of an ordinary search, bounded exactly like <see cref="IBookMetadataProvider.SearchAsync"/> but admitted through the budget.</summary>
    Task<BudgetedRead<BookCandidateSearchPage>> SearchBudgetedAsync(
        BookSearchQuery query, IDiscoveryBudget budget, CancellationToken cancellationToken);

    /// <summary>One record by the provider's own id. Ancillary lookups the ordinary detail call makes are not made here.</summary>
    Task<BudgetedRead<BookCandidate>> GetDetailsBudgetedAsync(
        string externalId, IDiscoveryBudget budget, CancellationToken cancellationToken);
}
