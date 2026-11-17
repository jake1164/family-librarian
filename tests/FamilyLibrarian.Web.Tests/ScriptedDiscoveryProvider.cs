using System.Collections.Concurrent;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A deterministic metadata provider that also implements the shared discovery interface, so host tests can drive explicit
/// source lookups without a network. Every discovery call is counted per operation, spends from the budget the host gave it the
/// way a real adapter does (an attempt before it answers, a page for a paged operation), and can be held open to test
/// concurrency and cancellation. Nothing it returns is a provider response; it is scripted data.
/// </summary>
internal sealed class ScriptedDiscoveryProvider(
    string id = "demo", string name = "Demo Source", BookCandidate[]? books = null) : IBookMetadataProvider, IBookMetadataDiscoveryProvider
{
    public static readonly SourceRef SagaSeries = new("demo", "S1", ExternalReferenceEntityType.Series);
    public static readonly SourceRef UnnamedSeries = new("demo", "S2", ExternalReferenceEntityType.Series);
    public static readonly SourceRef Herbert = new("demo", "A1", ExternalReferenceEntityType.Author);
    public static readonly SourceRef OtherHerbert = new("demo", "A2", ExternalReferenceEntityType.Author);

    private readonly ConcurrentDictionary<DiscoveryOperation, int> calls = new();
    private int searches;
    private int details;

    public string Id => id;

    public string DisplayName => name;

    string IBookMetadataDiscoveryProvider.ProviderId => id;

    public DiscoveryCapabilities Capabilities { get; set; } = new(
        Enum.GetValues<DiscoveryOperation>().Select(operation =>
            new KeyValuePair<DiscoveryOperation, DiscoveryCapability>(operation, DiscoveryCapability.Supported)));

    /// <summary>Held open until released: lets a test start a second lookup while the first is running, or cancel one in flight.</summary>
    public SemaphoreSlim? Gate { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Overrides the series-member page when set (for example to return a cut or a failure).</summary>
    public Func<DiscoveryCursor?, IDiscoveryBudget, DiscoveryResult<SeriesMember>>? MembersScript { get; set; }

    /// <summary>Overrides the author-works answer when set (for example to credit an illustrator).</summary>
    public Func<AuthorRef, IDiscoveryBudget, DiscoveryResult<AuthorWork>>? WorksScript { get; set; }

    public int Searches => Volatile.Read(ref searches);

    public int Details => Volatile.Read(ref details);

    public int DiscoveryCalls => calls.Values.Sum();

    public int Calls(DiscoveryOperation operation) => calls.TryGetValue(operation, out var count) ? count : 0;

    public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref searches);
        return Task.FromResult(new BookCandidateSearchPage(books ?? [], false));
    }

    public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref details);
        return Task.FromResult((books ?? []).FirstOrDefault(book => book.ExternalId == externalId));
    }

    public static DiscoveredBook Book(string externalId, string title, params CreditRef[] credits) =>
        new(new SourceRef("demo", externalId, ExternalReferenceEntityType.Work), new TitleForm(title), credits,
            PublicationEvidence.Dated(new DateOnly(1965, 1, 1), DatePrecision.Year, DateBasis.Original),
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    public async Task<DiscoveryResult<BookSeriesClaim>> FindSeriesClaimsForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        await BeginAsync(DiscoveryOperation.FindSeriesForBook, budget, cancellationToken);
        budget.RecordItems(2);
        return DiscoveryResult.Found<BookSeriesClaim>(
            [
                new BookSeriesClaim(new SeriesRef("Saga", SagaSeries), new PositionClaim("1", 1m, null, SagaSeries)),
                new BookSeriesClaim(new SeriesRef(string.Empty, UnnamedSeries), new PositionClaim("7", 7m, null, UnnamedSeries))
            ],
            new DiscoveryScope(DiscoveryOperation.FindSeriesForBook, book, "scripted-claims"),
            DiscoveryCompletion.ExhaustedInCheckedScope, Observed, budget.Usage, notes: ["series-name-not-provided"]);
    }

    public async Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
        DiscoveryResultProjection.SeriesOnly(await FindSeriesClaimsForBookAsync(book, budget, cancellationToken));

    public async Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(
        SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        // A real adapter reserves its page before it dispatches, so a refused page is never a provider call.
        if (!budget.TryAdmitPage())
        {
            return DiscoveryResult.NotAnswered<SeriesMember>(
                DiscoveryOutcome.Failed, new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, series.Id), Observed, budget.Usage, [DiscoveryCodes.PageCeiling]);
        }

        await BeginAsync(DiscoveryOperation.GetSeriesMembers, budget, cancellationToken);

        if (MembersScript is not null) return MembersScript(cursor, budget);
        var scope = new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, series.Id, "scripted-members", "scripted-order");
        SeriesMember Member(string externalId, string title, decimal position) =>
            new(Book(externalId, title, new CreditRef(0, "Frank Herbert", null, Herbert)), series.Id,
                [new PositionClaim(position.ToString(System.Globalization.CultureInfo.InvariantCulture), position, null, series.Id)]);
        budget.RecordItems(2);
        return cursor is null
            ? DiscoveryResult.Found<SeriesMember>(
                [Member("m1", "Member One", 1), Member("m2", "Member Two", 2)], scope,
                DiscoveryCompletion.MoreAvailable, Observed, budget.Usage, new DiscoveryCursor("p:2"))
            : DiscoveryResult.Found<SeriesMember>(
                [Member("m3", "Member Three", 3)], scope, DiscoveryCompletion.ExhaustedInCheckedScope, Observed, budget.Usage);
    }

    public async Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(
        AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        await BeginAsync(DiscoveryOperation.ResolveAuthor, budget, cancellationToken);
        return DiscoveryResult.Choices<AuthorRef>(
            [new AuthorRef("Frank Herbert", Herbert, "8 October 1920 – 11 February 1986", 141), new AuthorRef("Frank Herbert", OtherHerbert, "born 1872", 21)],
            new DiscoveryScope(DiscoveryOperation.ResolveAuthor, null), Observed, budget.Usage, [DiscoveryCodes.AnchorInsufficient]);
    }

    public async Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        await BeginAsync(DiscoveryOperation.GetAuthorWorks, budget, cancellationToken);
        if (WorksScript is not null) return WorksScript(author, budget);
        return DiscoveryResult.Found<AuthorWork>(
            [new AuthorWork(Book("w1", "Work One", new CreditRef(0, "Frank Herbert", null, author.Id)),
                [new BookSeriesClaim(new SeriesRef("Saga", SagaSeries), new PositionClaim("1", 1m, null, SagaSeries))])],
            new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id, "scripted-works"),
            DiscoveryCompletion.ExhaustedInCheckedScope, Observed, budget.Usage);
    }

    public async Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        await BeginAsync(DiscoveryOperation.GetAuthorSeries, budget, cancellationToken);
        return DiscoveryResult.Found<AuthorSeriesEntry>(
            [new AuthorSeriesEntry(new SeriesRef("Saga", SagaSeries), RelationshipEvidence.Derived("author-works-checked-pages"))],
            new DiscoveryScope(DiscoveryOperation.GetAuthorSeries, author.Id, "scripted-derived"),
            DiscoveryCompletion.ExhaustedInCheckedScope, Observed, budget.Usage);
    }

    private static DateTimeOffset Observed => new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private async Task BeginAsync(DiscoveryOperation operation, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        calls.AddOrUpdate(operation, 1, (_, count) => count + 1);
        Entered.TrySetResult();
        if (Gate is not null) await Gate.WaitAsync(cancellationToken);
        budget.TryAdmitAttempt(isRetry: false);
    }
}
