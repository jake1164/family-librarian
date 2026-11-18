// Expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Catalog;
using Claim = FamilyLibrarian.Application.Catalog.Discovery.PositionClaim;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The production <see cref="CatalogAnswerService"/> over a scripted source, in-memory repositories and a pinned clock, with the
/// real journey, admission limiter and context singletons. Every datum is fictional; physical attempts are counted where the scripted
/// source spends them from the journey budget, exactly as a real adapter does. Nothing here is a provider response.
/// </summary>
[TestClass]
public sealed class CatalogAnswerServiceTests
{
    private static readonly SourceRef SagaSeries = new("fiction", "S1", ExternalReferenceEntityType.Series);
    private static readonly SourceRef WriterAuthor = new("fiction", "A1", ExternalReferenceEntityType.Author);
    private static readonly SourceRef NamesakeAuthor = new("fiction", "A2", ExternalReferenceEntityType.Author);
    private static readonly CatalogAnswerCaller Reader = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), CatalogAnswerTransport.Web, "session-a");

    private static SourceRef Work(string id) => new("fiction", id, ExternalReferenceEntityType.Work);

    private static CatalogSubjectRef Saga(SeriesOrderingKind order = SeriesOrderingKind.Unspecified) =>
        CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Saga", SagaSeries, order);

    private static CatalogSubjectRef Writer() => CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, "A. Writer", WriterAuthor);

    private static DiscoveredBook Book(string id, string title, DateOnly? date, DateBasis basis = DateBasis.Original, PublicationStatus status = PublicationStatus.Unknown, params CreditRef[] credits) =>
        new(Work(id), new TitleForm(title), credits,
            date is null ? PublicationEvidence.NotStated : PublicationEvidence.Dated(date.Value, DatePrecision.Day, basis, status),
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    private static SeriesMember Member(string id, string title, DateOnly? date, decimal? position, DateBasis basis = DateBasis.Original, PublicationStatus status = PublicationStatus.Unknown) =>
        new(Book(id, title, date, basis, status), SagaSeries, position is null ? [] : [new Claim(position.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), position, null, SagaSeries)]);

    private static SeriesMember[] SagaMembers() =>
    [
        Member("w1", "Alder", new DateOnly(2001, 5, 1), 1),
        Member("w2", "Birch", new DateOnly(2005, 3, 9), 2),
        Member("w3", "Cedar", new DateOnly(2003, 8, 2), 3)
    ];

    private sealed class TestClock : TimeProvider, IClock
    {
        private long ticks = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>One scripted source that spends from the budget it is given the way a real adapter does, and counts what it spent.</summary>
    private sealed class Source : IBookMetadataProvider, IBookMetadataDiscoveryProvider, IBudgetedBookMetadataProvider
    {
        private readonly ConcurrentDictionary<DiscoveryOperation, int> reads = new();
        private int physicalAttempts;

        public string Id => "fiction";

        public string DisplayName => "Fiction Source";

        string IBookMetadataDiscoveryProvider.ProviderId => Id;

        string IBudgetedBookMetadataProvider.ProviderId => Id;

        public DiscoveryCapabilities Capabilities { get; set; } = new(
            Enum.GetValues<DiscoveryOperation>().Where(operation => operation.IsDiscoveryCapability())
                .Select(operation => new KeyValuePair<DiscoveryOperation, DiscoveryCapability>(operation, DiscoveryCapability.Supported)));

        /// <summary>How many physical attempts each read spends (more than one models a charged retry).</summary>
        public int AttemptsPerRead { get; set; } = 1;

        public Func<DiscoveryCursor?, SeriesMember[]> Members { get; set; } = _ => SagaMembers();

        public Func<DiscoveryCursor?, (SeriesMember[] Items, DiscoveryCursor? Next)>? PagedMembers { get; set; }

        public Func<AuthorWork[]> Works { get; set; } = () => [];

        public Func<AuthorSeriesEntry[]> AuthorSeriesItems { get; set; } = () => [];

        public Func<BookSearchQuery, BookCandidate[]> SearchHits { get; set; } = _ => [];

        public Func<BookSeriesClaim[]> Claims { get; set; } = () => [];

        public DiscoveryOutcome? ForcedOutcome { get; set; }

        public Func<CancellationToken, Task>? Hold { get; set; }

        public int PhysicalAttempts => Volatile.Read(ref physicalAttempts);

        public int Reads(DiscoveryOperation operation) => reads.TryGetValue(operation, out var count) ? count : 0;

        public int TotalReads => reads.Values.Sum();

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException("The ordinary, unbudgeted search must never be called by an answer.");

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) => throw new InvalidOperationException("The ordinary, unbudgeted detail must never be called by an answer.");

        private async Task<(bool Ok, string? Stop)> BeginAsync(DiscoveryOperation operation, IDiscoveryBudget budget, bool paged, CancellationToken ct)
        {
            reads.AddOrUpdate(operation, 1, (_, count) => count + 1);
            if (Hold is not null) await Hold(ct);
            if (paged && !budget.TryAdmitPage()) return (false, DiscoveryCodes.PageCeiling);
            for (var attempt = 0; attempt < AttemptsPerRead; attempt++)
            {
                if (budget.TryAdmitAttempt(isRetry: attempt > 0) != AttemptAdmission.Admitted) return (false, DiscoveryCodes.AttemptCeiling);
                Interlocked.Increment(ref physicalAttempts);
            }

            return (true, null);
        }

        private DiscoveryResult<T> Answer<T>(DiscoveryOperation operation, SourceRef? subject, IDiscoveryBudget budget, T[] items, DiscoveryCursor? next = null)
        {
            var scope = new DiscoveryScope(operation, subject);
            var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
            budget.RecordItems(items.Length);
            return ForcedOutcome switch
            {
                DiscoveryOutcome.TimedOut when items.Length > 0 => DiscoveryResult.Interrupted(DiscoveryOutcome.TimedOut, items, scope, now, budget.Usage, [DiscoveryCodes.DeadlineReached]),
                DiscoveryOutcome.Throttled when items.Length > 0 => DiscoveryResult.Interrupted(DiscoveryOutcome.Throttled, items, scope, now, budget.Usage, [DiscoveryCodes.Throttled]),
                { } outcome when outcome is not (DiscoveryOutcome.TimedOut or DiscoveryOutcome.Throttled) || items.Length == 0 =>
                    DiscoveryResult.NotAnswered<T>(outcome, scope, now, budget.Usage, [outcome == DiscoveryOutcome.TimedOut ? DiscoveryCodes.DeadlineReached : outcome == DiscoveryOutcome.Throttled ? DiscoveryCodes.Throttled : DiscoveryCodes.ProviderError]),
                _ when items.Length == 0 => DiscoveryResult.Empty<T>(scope, now, budget.Usage),
                _ => DiscoveryResult.Found(items, scope, next is null ? DiscoveryCompletion.ExhaustedInCheckedScope : DiscoveryCompletion.MoreAvailable, now, budget.Usage, next)
            };
        }

        private static DiscoveryResult<T> Stopped<T>(DiscoveryOperation operation, SourceRef? subject, IDiscoveryBudget budget, string code) =>
            DiscoveryResult.NotAnswered<T>(DiscoveryOutcome.Failed, new DiscoveryScope(operation, subject), DateTimeOffset.UnixEpoch, budget.Usage, [code]);

        public async Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            var (ok, stop) = await BeginAsync(DiscoveryOperation.GetSeriesMembers, budget, paged: true, cancellationToken);
            if (!ok) return Stopped<SeriesMember>(DiscoveryOperation.GetSeriesMembers, series.Id, budget, stop!);
            if (PagedMembers is { } paged)
            {
                var (items, next) = paged(cursor);
                return Answer(DiscoveryOperation.GetSeriesMembers, series.Id, budget, items, next);
            }

            return Answer(DiscoveryOperation.GetSeriesMembers, series.Id, budget, Members(cursor));
        }

        public async Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            var (ok, stop) = await BeginAsync(DiscoveryOperation.GetAuthorWorks, budget, paged: true, cancellationToken);
            return ok ? Answer(DiscoveryOperation.GetAuthorWorks, author.Id, budget, Works()) : Stopped<AuthorWork>(DiscoveryOperation.GetAuthorWorks, author.Id, budget, stop!);
        }

        public async Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            var (ok, stop) = await BeginAsync(DiscoveryOperation.GetAuthorSeries, budget, paged: true, cancellationToken);
            return ok ? Answer(DiscoveryOperation.GetAuthorSeries, author.Id, budget, AuthorSeriesItems()) : Stopped<AuthorSeriesEntry>(DiscoveryOperation.GetAuthorSeries, author.Id, budget, stop!);
        }

        public async Task<DiscoveryResult<BookSeriesClaim>> FindSeriesClaimsForBookAsync(SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            var (ok, stop) = await BeginAsync(DiscoveryOperation.FindSeriesForBook, budget, paged: false, cancellationToken);
            return ok ? Answer(DiscoveryOperation.FindSeriesForBook, book, budget, Claims()) : Stopped<BookSeriesClaim>(DiscoveryOperation.FindSeriesForBook, book, budget, stop!);
        }

        public Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by the answer service.");

        public Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by the answer service.");

        public async Task<BudgetedRead<BookCandidateSearchPage>> SearchBudgetedAsync(BookSearchQuery query, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            var (ok, stop) = await BeginAsync(DiscoveryOperation.SearchBooks, budget, paged: true, cancellationToken);
            var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
            if (!ok) return BudgetedRead.NotAnswered<BookCandidateSearchPage>(DiscoveryOutcome.Failed, budget.Usage, now, [stop!]);
            if (ForcedOutcome is { } forced && forced is not DiscoveryOutcome.Ok) return BudgetedRead.NotAnswered<BookCandidateSearchPage>(forced, budget.Usage, now, [DiscoveryCodes.ProviderError]);
            return BudgetedRead.Found(new BookCandidateSearchPage(SearchHits(query), false), budget.Usage, now);
        }

        public Task<BudgetedRead<BookCandidate>> GetDetailsBudgetedAsync(string externalId, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by the answer service.");
    }

    /// <summary>An active source with only the ordinary, unbudgeted contract: it cannot be used to seed an answer.</summary>
    private sealed class PlainSource : IBookMetadataProvider
    {
        public string Id => "plain";

        public string DisplayName => "Plain Source";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException("Never called by an answer.");

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) => throw new InvalidOperationException("Never called by an answer.");
    }

    private sealed class Resolver(Source source) : IActiveMetadataProviderResolver
    {
        private readonly PlainSource plain = new();

        public bool WithPlainSource { get; set; }

        public bool Enabled { get; set; } = true;

        public bool Registered { get; set; } = true;

        /// <summary>Runs while the source is being resolved, the way a slow settings read lets time pass.</summary>
        public Action? WhileResolving { get; set; }

        public Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IBookMetadataProvider>>(Enabled ? (WithPlainSource ? [source, plain] : [source]) : []);

        public Task<IBookMetadataProvider?> FindActiveProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            WhileResolving?.Invoke();
            return Task.FromResult<IBookMetadataProvider?>(Enabled && providerId == source.Id ? source : null);
        }

        public bool IsRegistered(string providerId) => Registered && providerId == source.Id;
    }

    private sealed class SavedRepository : ISavedCatalogEvidenceRepository
    {
        public int Reads { get; private set; }

        public List<SavedSeriesEntryEvidence> Series { get; } = [];

        public List<SavedWorkEvidence> AuthorWorks { get; } = [];

        public Task<IReadOnlyList<SavedSeriesEntryEvidence>> GetSeriesEvidenceAsync(Guid seriesId, int skip, int take, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<SavedSeriesEntryEvidence>>(Series.Skip(skip).Take(take).ToArray());
        }

        public Task<IReadOnlyList<SavedWorkEvidence>> GetAuthorEvidenceAsync(Guid authorId, int skip, int take, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<SavedWorkEvidence>>(AuthorWorks.Skip(skip).Take(take).ToArray());
        }
    }

    private sealed class BrowseRepository : ICatalogBrowseRepository
    {
        public List<BrowseNameChoice> SeriesChoices { get; } = [];

        public List<BrowseNameChoice> AuthorChoices { get; } = [];

        public Task<BrowseAuthorHeader?> FindAuthorAsync(Guid authorId, CancellationToken cancellationToken) => Task.FromResult<BrowseAuthorHeader?>(null);

        public Task<BrowseSeriesHeader?> FindSeriesAsync(Guid seriesId, CancellationToken cancellationToken) => Task.FromResult<BrowseSeriesHeader?>(null);

        public Task<IReadOnlyList<BrowseWork>> GetAuthorWorksAsync(Guid authorId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrowseWork>>([]);

        public Task<IReadOnlyList<BrowseSeriesMember>> GetSeriesMembersAsync(Guid seriesId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrowseSeriesMember>>([]);

        public Task<IReadOnlyList<BrowseNameChoice>> FindAuthorsByNormalizedNameAsync(string normalizedName, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrowseNameChoice>>(AuthorChoices.Take(take).ToArray());

        public Task<IReadOnlyList<BrowseNameChoice>> FindSeriesByNormalizedNameAsync(string normalizedName, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrowseNameChoice>>(SeriesChoices.Take(take).ToArray());
    }

    private sealed class Rig
    {
        public Rig(
            CatalogExpansionLimiter? limiter = null,
            CatalogAnswerOptions? options = null,
            CatalogAnswerContextLimits? limits = null,
            Action<Source>? configure = null)
        {
            Source = new Source();
            configure?.Invoke(Source);
            Resolver = new Resolver(Source);
            Contexts = new CatalogAnswerContextStore(limits ?? CatalogAnswerContextLimits.Default);
            Journeys = new CatalogDiscoveryJourneys();
            Limiter = limiter ?? new CatalogExpansionLimiter();
            Service = new CatalogAnswerService(Saved, Browse, Resolver, Contexts, Journeys, Limiter, Clock, Clock, options ?? CatalogAnswerOptions.Default);
        }

        public TestClock Clock { get; } = new();

        public Source Source { get; }

        public Resolver Resolver { get; }

        public SavedRepository Saved { get; } = new();

        public BrowseRepository Browse { get; } = new();

        public CatalogAnswerContextStore Contexts { get; }

        public CatalogDiscoveryJourneys Journeys { get; }

        public CatalogExpansionLimiter Limiter { get; }

        public CatalogAnswerService Service { get; }

        public Task<CatalogAnswer> Ask(CatalogAnswerRequest request, CatalogAnswerCaller? caller = null, CancellationToken cancellationToken = default) =>
            Service.AnswerAsync(caller ?? Reader, request, cancellationToken);
    }

    private static CatalogAnswerRequest Latest(CatalogSubjectRef? subject = null) =>
        new() { Operation = CatalogAnswerOperation.LatestPublished, Subject = subject ?? Saga() };

    // ====================================================================================== a fresh lookup

    [TestMethod]
    public async Task ALatestQuestionReadsOneSeriesPageAndAccountsForExactlyThatRead()
    {
        var rig = new Rig();

        var answer = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.Answered, answer.Outcome);
        Assert.AreEqual("Birch", answer.Entries.Single().Title, "Latest by evidenced date, not the highest number (Cedar is book 3).");
        Assert.AreEqual(1, rig.Source.TotalReads);
        Assert.AreEqual(1, rig.Source.PhysicalAttempts);
        Assert.AreEqual(1, answer.Usage.ProviderReads);
        Assert.AreEqual(1, answer.Usage.Attempts);
        Assert.AreEqual(1, answer.Usage.Pages);
        Assert.AreEqual(new DateOnly(2026, 11, 17), answer.Today);
        Assert.IsTrue(answer.Coverage.ExhaustedInCheckedScope);
        Assert.AreEqual("fiction", answer.Coverage.Sources.Single().ProviderId);
        Assert.AreEqual(CatalogSourceState.Checked, answer.Coverage.Sources.Single().State);
        Assert.IsNotNull(answer.Context);
        Assert.IsNotNull(answer.Context.ToJson());
    }

    [TestMethod]
    public async Task EveryPhysicalAttemptIncludingARetryIsCountedInTheAnswersUsage()
    {
        var rig = new Rig(configure: source => source.AttemptsPerRead = 2);

        var answer = await rig.Ask(Latest());

        Assert.AreEqual(2, rig.Source.PhysicalAttempts);
        Assert.AreEqual(2, answer.Usage.Attempts);
        Assert.AreEqual(1, answer.Usage.Retries, "The second attempt was the operation's one retry.");
    }

    [TestMethod]
    public async Task AWorkWithAnUnverifiedBasisIsQualifiedAndTheSourceStatusIsNeverInvented()
    {
        var rig = new Rig(configure: source => source.Members = _ =>
        [
            Member("w1", "Alder", new DateOnly(2001, 5, 1), 1),
            Member("w9", "Fir", new DateOnly(2027, 5, 11), 4, DateBasis.Unknown)
        ]);

        var upcoming = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.NextRelease, Subject = Saga() });

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, upcoming.Outcome);
        Assert.AreEqual("Fir", upcoming.Entries.Single().Title);
        Assert.AreEqual(PublicationStatus.Unknown, upcoming.Entries.Single().Publication.SourceStatus);
        Assert.IsTrue(upcoming.HasQualifier(CatalogAnswerCodes.StatusUnverified));
        Assert.IsTrue(upcoming.HasQualifier(CatalogAnswerCodes.DateBasisUnverified));
    }

    [TestMethod]
    public async Task APositionQuestionFindsTheClaimedPositionAndNeverPadsAMissingOne()
    {
        var rig = new Rig();

        var found = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, Subject = Saga(), Position = 3 });
        var missing = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, Subject = Saga(), Position = 25 });

        Assert.AreEqual("Cedar", found.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, missing.Outcome);
        Assert.AreEqual(0, missing.Entries.Count);
    }

    [TestMethod]
    public async Task ASeriesSubjectIsNeverTreatedAsAnAuthorAndAnAuthorQuestionNeedsAnAuthor()
    {
        var rig = new Rig();

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Subject = Saga() });
        var noSubject = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks });

        Assert.AreEqual(CatalogAnswerOutcome.InsufficientEvidence, answer.Outcome);
        CollectionAssert.Contains(answer.Reasons.ToArray(), CatalogAnswerCodes.SubjectInvalid);
        CollectionAssert.Contains(noSubject.Reasons.ToArray(), CatalogAnswerCodes.SubjectRequired);
        Assert.AreEqual(0, rig.Source.TotalReads, "Rejected before any read.");
    }

    [TestMethod]
    public async Task AuthorWorksKeepTheCreditedAuthorApartFromANamesakeAndAreNeverExhaustive()
    {
        var mine = new AuthorWork(Book("m1", "Mine", new DateOnly(2001, 1, 1), DateBasis.Original, PublicationStatus.Unknown, new CreditRef(0, "A. Writer", null, WriterAuthor)), []);
        var theirs = new AuthorWork(Book("n1", "Namesake Book", new DateOnly(2002, 1, 1), DateBasis.Original, PublicationStatus.Unknown, new CreditRef(0, "A. Writer", null, NamesakeAuthor)), []);
        var rig = new Rig(configure: source => source.Works = () => [mine, theirs]);

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Subject = Writer() });

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, answer.Outcome);
        Assert.AreEqual("Mine", answer.Entries.Single().Title);
        Assert.AreEqual("Namesake Book", answer.Groups.Single(group => group.Code == "credit-unverified").Entries.Single().Title);
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.ListNotExhaustive));
    }

    [TestMethod]
    public async Task AuthorSeriesKeepsTheEdgeBasisAndRetainsNothingAsAnAnswerEntry()
    {
        var series = new AuthorSeriesEntry(new SeriesRef("The Saga", SagaSeries), RelationshipEvidence.Derived("author-works-first-page"));
        var rig = new Rig(configure: source => source.AuthorSeriesItems = () => [series]);

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorSeries, Subject = Writer() });

        Assert.AreEqual("The Saga", answer.Series.Single().Label);
        Assert.AreEqual(RelationshipBasis.Derived, answer.Series.Single().Relationship.Basis);
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.DerivedEdges));
        Assert.AreEqual(1, rig.Source.Reads(DiscoveryOperation.GetAuthorSeries));
    }

    // ========================================================================= follow-ups from the retained root

    [TestMethod]
    public async Task AFollowUpIsAnsweredFromTheRetainedEntriesWithNoProviderCallAndNoAdmission()
    {
        var rig = new Rig(limiter: new CatalogExpansionLimiter(maxStarts: 1, TimeSpan.FromMinutes(1)));
        var first = await rig.Ask(Latest());
        var reads = rig.Source.TotalReads;
        var attempts = rig.Source.PhysicalAttempts;

        var next = await rig.Ask(new CatalogAnswerRequest
        {
            Operation = CatalogAnswerOperation.AfterAnchor,
            ContextRootId = first.Context!.RootId,
            Anchor = CatalogSubjectRef.FromSource(CatalogSubjectKind.Book, "Alder", Work("w1"))
        });
        var position = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, ContextRootId = first.Context.RootId, Position = 2 });

        Assert.AreEqual("Birch", next.Entries.Single().Title);
        Assert.AreEqual("Birch", position.Entries.Single().Title);
        Assert.AreEqual(reads, rig.Source.TotalReads, "A follow-up spends no provider read.");
        Assert.AreEqual(attempts, rig.Source.PhysicalAttempts);
        Assert.AreEqual(0, next.Usage.ProviderReads);
        Assert.AreEqual(0, next.Usage.Attempts);
        Assert.AreEqual(first.Context.RootId, next.Context!.RootId);
        // The one allowed start was spent by the first lookup only; a follow-up needs none, so a rendering never starves the user.
        Assert.AreEqual(CatalogAnswerOutcome.Answered, (await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, ContextRootId = first.Context.RootId, Position = 1 })).Outcome);
    }

    [TestMethod]
    public async Task ARootBelongsToItsUserTransportAndSessionAndForeignAskersGetTheSameAnswerAsExpiry()
    {
        var rig = new Rig();
        var first = await rig.Ask(Latest());
        var reads = rig.Source.TotalReads;
        var request = new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, ContextRootId = first.Context!.RootId, Position = 1 };

        foreach (var stranger in new[]
        {
            new CatalogAnswerCaller(Guid.NewGuid(), Reader.Transport, Reader.ScopeKey),
            new CatalogAnswerCaller(Reader.OwnerId, CatalogAnswerTransport.Matrix, Reader.ScopeKey),
            new CatalogAnswerCaller(Reader.OwnerId, Reader.Transport, "another-session-or-room")
        })
        {
            var refused = await rig.Ask(request, stranger);
            Assert.AreEqual(CatalogAnswerOutcome.ContextExpired, refused.Outcome);
            Assert.AreEqual(0, refused.Entries.Count, "Nothing of another caller's context is shown.");
            Assert.AreEqual(CatalogAnswerUsage.None.ProviderReads, refused.Usage.ProviderReads);
        }

        Assert.AreEqual(reads, rig.Source.TotalReads, "A rejected follow-up calls and spends nothing.");
        Assert.AreEqual(CatalogAnswerOutcome.Answered, (await rig.Ask(request)).Outcome, "The rightful caller is unaffected.");
    }

    [TestMethod]
    public async Task AnExpiredRootAsksToRestartAndNeverSilentlyRefetchesOrChoosesANamesake()
    {
        var rig = new Rig();
        var first = await rig.Ask(Latest());
        var reads = rig.Source.TotalReads;
        rig.Clock.Advance(TimeSpan.FromMinutes(10));

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, ContextRootId = first.Context!.RootId, Position = 1 });

        Assert.AreEqual(CatalogAnswerOutcome.ContextExpired, answer.Outcome);
        CollectionAssert.Contains(answer.Reasons.ToArray(), CatalogAnswerCodes.ContextExpiredReason);
        Assert.AreEqual(reads, rig.Source.TotalReads);
    }

    [TestMethod]
    public async Task ARootWithNoRetainedWindowIsExpiredNotAnEmptyAnswer()
    {
        var rig = new Rig(limits: new CatalogAnswerContextLimits { MaxBytesPerRoot = 10 });

        var first = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.Answered, first.Outcome, "The answer itself is unaffected by what could be retained.");
        Assert.IsNull(first.Context);
        CollectionAssert.Contains(first.Reasons.ToArray(), CatalogAnswerCodes.ContextNotRetained);
    }

    [TestMethod]
    public async Task ANewSubjectInsideTheSameRootIsANewLookupOnTheSameJourney()
    {
        var rig = new Rig(configure: source =>
        {
            source.Works = () => [new AuthorWork(Book("m1", "Mine", new DateOnly(2001, 1, 1), DateBasis.Original, PublicationStatus.Unknown, new CreditRef(0, "A. Writer", null, WriterAuthor)), [])];
        });
        var first = await rig.Ask(Latest());

        var author = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Subject = Writer(), ContextRootId = first.Context!.RootId });

        Assert.AreEqual("Mine", author.Entries.Single().Title);
        Assert.AreEqual(first.Context.RootId, author.Context!.RootId);
        Assert.AreEqual(1, rig.Journeys.Count, "Both lookups drew on one journey allowance.");
        Assert.AreEqual(2, rig.Source.TotalReads);
    }

    // ======================================================================== continuation spends the same journey

    private static (SeriesMember[] Items, DiscoveryCursor? Next) Page(DiscoveryCursor? cursor) => cursor?.Value switch
    {
        null => ([Member("w1", "Alder", new DateOnly(2001, 5, 1), 1), Member("w2", "Birch", new DateOnly(2005, 3, 9), 2)], new DiscoveryCursor("p2")),
        "p2" => ([Member("w3", "Cedar", new DateOnly(2003, 8, 2), 3), Member("w4", "Dogwood", new DateOnly(2009, 6, 1), 4)], null),
        _ => ([], null)
    };

    [TestMethod]
    public async Task ContinuingReadsOnePageOnTheSameJourneyAndReevaluatesOverEverythingRetained()
    {
        var rig = new Rig(configure: source => source.PagedMembers = Page);
        var first = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, first.Outcome);
        Assert.IsTrue(first.Coverage.MoreAvailable);
        Assert.IsFalse(first.Coverage.ExhaustedInCheckedScope);
        Assert.AreEqual("Birch", first.Entries.Single().Title, "Only the latest among the known entries so far.");
        Assert.IsNotNull(first.ContinuationToken);

        var second = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = first.Context!.RootId, ContinuationToken = first.ContinuationToken });

        Assert.AreEqual("Dogwood", second.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Answered, second.Outcome);
        Assert.IsTrue(second.Coverage.ExhaustedInCheckedScope);
        Assert.IsNull(second.ContinuationToken);
        Assert.AreEqual(2, rig.Source.Reads(DiscoveryOperation.GetSeriesMembers));
        Assert.AreEqual(2, rig.Source.PhysicalAttempts);
        Assert.AreEqual(1, rig.Journeys.Count, "The continuation spent the same journey, not a fresh allowance.");
    }

    [TestMethod]
    public async Task AForgedStaleOrForeignContinuationIsRefusedWithNoCallAndNoCharge()
    {
        var rig = new Rig(configure: source => source.PagedMembers = Page);
        var first = await rig.Ask(Latest());
        var reads = rig.Source.TotalReads;
        var other = await rig.Ask(Latest(), new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "other"));

        var forged = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = first.Context!.RootId, ContinuationToken = "FORGED" });
        var crossRoot = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = first.Context.RootId, ContinuationToken = other.ContinuationToken });
        var foreignRoot = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = other.Context!.RootId, ContinuationToken = other.ContinuationToken }, Reader);

        CollectionAssert.Contains(forged.Reasons.ToArray(), CatalogAnswerCodes.ContinuationUnavailable);
        CollectionAssert.Contains(crossRoot.Reasons.ToArray(), CatalogAnswerCodes.ContinuationUnavailable);
        Assert.AreEqual(CatalogAnswerOutcome.ContextExpired, foreignRoot.Outcome);
        Assert.AreEqual(reads + 1, rig.Source.TotalReads, "Only the other caller's own first lookup read anything.");
    }

    [TestMethod]
    public async Task TheJourneyAttemptCeilingStopsALaterPageAsAFailureNeverAsAnEmptyResult()
    {
        // Two attempts per read (an attempt and its one retry): six lookups in one root spend the journey's 12, the seventh is refused before dispatch.
        var rig = new Rig(configure: source => source.AttemptsPerRead = 2);
        CatalogSubjectRef SeriesNo(int number) =>
            CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "Series " + number, new SourceRef("fiction", "S" + number, ExternalReferenceEntityType.Series));
        var answer = await rig.Ask(Latest(SeriesNo(1)));
        var root = answer.Context!.RootId;
        for (var number = 2; number <= 6; number++)
        {
            answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = SeriesNo(number), ContextRootId = root });
            Assert.AreEqual(CatalogAnswerOutcome.Answered, answer.Outcome, "Lookup " + number);
        }

        Assert.AreEqual(12, rig.Source.PhysicalAttempts);
        var refused = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = SeriesNo(7), ContextRootId = root });

        Assert.AreEqual(12, rig.Source.PhysicalAttempts, "The seventh read was refused before any attempt was dispatched.");
        Assert.AreEqual(CatalogAnswerOutcome.Failed, refused.Outcome);
        CollectionAssert.Contains(refused.Reasons.ToArray(), DiscoveryCodes.AttemptCeiling);
        Assert.AreEqual(0, refused.Entries.Count);
        Assert.AreEqual(1, rig.Journeys.Count, "Every lookup drew on the one allowance of the root's journey.");
    }

    [TestMethod]
    public async Task ContinuingIsBoundByTheOperationsOwnFivePageAllowanceNotAFreshOne()
    {
        var rig = new Rig(configure: source => source.PagedMembers = cursor =>
        {
            var page = cursor is null ? 1 : int.Parse(cursor.Value[1..], System.Globalization.CultureInfo.InvariantCulture);
            return ([Member("w" + page, "Book " + page, new DateOnly(2000 + page, 1, 1), page)], new DiscoveryCursor("p" + (page + 1)));
        });
        var answer = await rig.Ask(Latest());
        var root = answer.Context!.RootId;
        for (var page = 2; page <= 5; page++)
        {
            answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = root, ContinuationToken = answer.ContinuationToken });
            Assert.AreEqual(CatalogAnswerOutcome.Qualified, answer.Outcome, "Page " + page);
        }

        var sixth = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = root, ContinuationToken = answer.ContinuationToken });

        Assert.AreEqual(5, rig.Source.PhysicalAttempts, "Five pages, five attempts; the sixth was refused before dispatch.");
        Assert.AreEqual(CatalogAnswerOutcome.Failed, sixth.Outcome);
        CollectionAssert.Contains(sixth.Reasons.ToArray(), DiscoveryCodes.PageCeiling);
    }

    // ================================================================================================= admission

    [TestMethod]
    public async Task AnAnswerSharesTheOneRunningSlotAndTheStartAllowanceWithEveryOtherLookup()
    {
        var limiter = new CatalogExpansionLimiter(maxStarts: 1, TimeSpan.FromMinutes(1));
        var rig = new Rig(limiter: limiter);

        var first = await rig.Ask(Latest());
        var second = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.Answered, first.Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.Throttled, second.Outcome);
        CollectionAssert.Contains(second.Reasons.ToArray(), CatalogAnswerCodes.AdmissionRefused);
        Assert.IsTrue(second.RetryAfter > TimeSpan.Zero);
        Assert.AreEqual(1, rig.Source.TotalReads, "A refused lookup makes no provider call.");
        Assert.AreEqual(CatalogSourceState.NotQueried, second.Coverage.Sources.Single().State);

        var otherOwner = await rig.Ask(Latest(), new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Matrix, "!room:example"));
        Assert.AreEqual(CatalogAnswerOutcome.Answered, otherOwner.Outcome, "The allowance is per user.");
    }

    [TestMethod]
    public async Task ARunningLookupHoldsTheOwnersSlotWithNoQueueAndReleasesItWhenDone()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig(configure: source => source.Hold = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });

        var running = rig.Ask(Latest());
        await entered.Task;
        var concurrent = await rig.Ask(Latest());
        release.SetResult();
        var finished = await running;
        var afterwards = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.Refused, concurrent.Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.Answered, finished.Outcome);
        Assert.AreEqual(CatalogAnswerOutcome.Answered, afterwards.Outcome, "The slot was released.");
        Assert.AreEqual(2, rig.Source.TotalReads, "The refused concurrent lookup made no read.");
    }

    [TestMethod]
    public async Task APermitTakenByAnotherLookupOfTheSameUserRefusesTheAnswerBeforeAnyRead()
    {
        var rig = new Rig();
        Assert.AreEqual(ExpansionAdmission.Admitted, rig.Limiter.TryStart(Reader.OwnerId, rig.Clock.UtcNow, out var webLookup, out _));
        using (webLookup)
        {
            var answer = await rig.Ask(Latest());

            Assert.AreEqual(CatalogAnswerOutcome.Refused, answer.Outcome);
            Assert.AreEqual(0, rig.Source.TotalReads);
        }
    }

    // ============================================================================ typed failures, never "empty"

    [TestMethod]
    public async Task EachSourceFailureIsItsOwnOutcomeAndNeverNothingFound()
    {
        foreach (var (forced, expected) in new[]
        {
            (DiscoveryOutcome.TimedOut, CatalogAnswerOutcome.TimedOut),
            (DiscoveryOutcome.Throttled, CatalogAnswerOutcome.Throttled),
            (DiscoveryOutcome.Failed, CatalogAnswerOutcome.Failed),
            (DiscoveryOutcome.Unavailable, CatalogAnswerOutcome.Unavailable)
        })
        {
            // A source that returned nothing before it failed: with no items the failure is the whole answer.
            var rig = new Rig(configure: source =>
            {
                source.ForcedOutcome = forced;
                source.Members = _ => [];
            });

            var answer = await rig.Ask(Latest());

            Assert.AreEqual(expected, answer.Outcome, forced.ToString());
            Assert.AreEqual(0, answer.Entries.Count);
            Assert.AreNotEqual(CatalogAnswerOutcome.NoVerifiedMatch, answer.Outcome);
            Assert.IsTrue(answer.Coverage.HasSourceGaps);
            Assert.IsNull(answer.Context, "A failure retains nothing a follow-up could mistake for an answer.");
        }
    }

    [TestMethod]
    public async Task AnInterruptedReadKeepsWhatWasObservedAsAQualifiedPartialNeverAPlainAnswer()
    {
        foreach (var forced in new[] { DiscoveryOutcome.TimedOut, DiscoveryOutcome.Throttled })
        {
            var rig = new Rig(configure: source => source.ForcedOutcome = forced);

            var answer = await rig.Ask(Latest());

            Assert.AreEqual(CatalogAnswerOutcome.Qualified, answer.Outcome, forced.ToString());
            Assert.AreEqual("Birch", answer.Entries.Single().Title);
            Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.SourceGaps));
            Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.IncompleteCoverage));
            Assert.IsFalse(answer.Coverage.ExhaustedInCheckedScope);
        }
    }

    [TestMethod]
    public async Task ADisabledOrRemovedOrUnsupportedSourceIsTypedAndNothingIsCalledOrCharged()
    {
        var disabled = new Rig();
        disabled.Resolver.Enabled = false;
        var answerDisabled = await disabled.Ask(Latest());
        Assert.AreEqual(CatalogAnswerOutcome.Unavailable, answerDisabled.Outcome);
        CollectionAssert.Contains(answerDisabled.Reasons.ToArray(), "source-disabled");
        Assert.AreEqual(0, disabled.Source.TotalReads);

        var removed = new Rig();
        removed.Resolver.Enabled = false;
        removed.Resolver.Registered = false;
        CollectionAssert.Contains((await removed.Ask(Latest())).Reasons.ToArray(), "source-removed");

        var unsupported = new Rig(
            limiter: new CatalogExpansionLimiter(maxStarts: 1, TimeSpan.FromMinutes(1)),
            configure: source => source.Capabilities = new DiscoveryCapabilities(
                [new KeyValuePair<DiscoveryOperation, DiscoveryCapability>(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Unsupported("not-in-api"))]));
        var answerUnsupported = await unsupported.Ask(Latest());
        Assert.AreEqual(CatalogAnswerOutcome.Unsupported, answerUnsupported.Outcome);
        CollectionAssert.Contains(answerUnsupported.Reasons.ToArray(), "not-in-api");
        Assert.AreEqual(0, unsupported.Source.TotalReads);
        Assert.AreEqual(
            ExpansionAdmission.Admitted,
            unsupported.Limiter.TryStart(Reader.OwnerId, unsupported.Clock.UtcNow, out var permit, out _),
            "The refusal came before admission, so the user's one allowed start is untouched.");
        permit?.Dispose();
    }

    [TestMethod]
    public async Task ASourceDisabledWhileBeingResolvedIsCaughtBeforeAnySpend()
    {
        var rig = new Rig();
        rig.Resolver.WhileResolving = () => rig.Resolver.Enabled = false;

        var answer = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.Unavailable, answer.Outcome);
        Assert.AreEqual(0, rig.Source.TotalReads);
    }

    [TestMethod]
    public async Task ARootThatEndsWhileTheSourceIsBeingResolvedIsRecheckedBeforeAnySpend()
    {
        var rig = new Rig();
        var first = await rig.Ask(Latest());
        var reads = rig.Source.TotalReads;
        var attemptsBefore = rig.Source.PhysicalAttempts;
        rig.Resolver.WhileResolving = () => rig.Clock.Advance(TimeSpan.FromMinutes(11));

        var answer = await rig.Ask(new CatalogAnswerRequest
        {
            Operation = CatalogAnswerOperation.LatestPublished,
            Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "Another", new SourceRef("fiction", "S2", ExternalReferenceEntityType.Series)),
            ContextRootId = first.Context!.RootId
        });

        Assert.AreEqual(CatalogAnswerOutcome.ContextExpired, answer.Outcome);
        Assert.AreEqual(reads, rig.Source.TotalReads);
        Assert.AreEqual(attemptsBefore, rig.Source.PhysicalAttempts);
    }

    // ============================================================================================= the deadline

    [TestMethod]
    public async Task TheOverallDeadlineEndsAStuckLookupAsATimeoutAndFreesTheUsersSlot()
    {
        var rig = new Rig(
            options: new CatalogAnswerOptions { Deadline = TimeSpan.FromMilliseconds(200) },
            configure: source => source.Hold = token => Task.Delay(Timeout.Infinite, token));

        var answer = await rig.Ask(Latest());

        Assert.AreEqual(CatalogAnswerOutcome.TimedOut, answer.Outcome);
        CollectionAssert.Contains(answer.Reasons.ToArray(), CatalogAnswerCodes.DeadlineReached);
        Assert.AreEqual(0, answer.Entries.Count);
        rig.Source.Hold = null;
        Assert.AreEqual(CatalogAnswerOutcome.Answered, (await rig.Ask(Latest())).Outcome, "The timed-out lookup released its permit.");
    }

    [TestMethod]
    public async Task ACallersCancellationPropagatesAndIsNeverAnAnswer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig(configure: source => source.Hold = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        using var cancel = new CancellationTokenSource();

        var running = rig.Ask(Latest(), cancellationToken: cancel.Token);
        await entered.Task;
        await cancel.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => running);
        rig.Source.Hold = null;
        Assert.AreEqual(CatalogAnswerOutcome.Answered, (await rig.Ask(Latest())).Outcome, "The cancelled lookup released its permit.");
    }

    // =========================================================================== choices, namesakes and seed reads

    private static BookCandidate Hit(string id, string title, params BookAuthorCredit[] credits) =>
        new("fiction", "Fiction Source", id, title, credits.Select(credit => credit.Name).ToArray(), null, null, null, [],
            [new BookSeriesCandidate("The Saga", "1", true, 1m) { SourceSeriesId = "S1" }])
        {
            AuthorCredits = credits,
            SourceKind = ExternalReferenceEntityType.Work
        };

    [TestMethod]
    public async Task ASeriesNameOnlyEverProducesChoicesAndFetchesNothingThatDependsOnTheSelection()
    {
        var rig = new Rig(configure: source => source.SearchHits = _ => [Hit("w1", "Alder", new BookAuthorCredit("A. Writer", null, "A1"))]);

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga" });

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, answer.Outcome, "Even a lone choice is for the user to confirm: a name never selects.");
        var choice = answer.Choices.Single();
        Assert.AreEqual(SagaSeries, choice.Reference.Native);
        Assert.AreEqual(0, rig.Source.Reads(DiscoveryOperation.GetSeriesMembers), "Clarification stops before any selection-dependent call.");
        Assert.AreEqual(1, rig.Source.Reads(DiscoveryOperation.SearchBooks));
        Assert.AreEqual(1, answer.Usage.ProviderReads);
        Assert.AreEqual(1, answer.Usage.Attempts);
        Assert.IsFalse(answer.Coverage.ExhaustedInCheckedScope, "A search is a sample.");
        Assert.IsNotNull(answer.Context);
        Assert.AreEqual(choice, rig.Contexts.TryGetChoice(Reader, answer.Context.RootId, 0, rig.Clock.UtcNow));
    }

    [TestMethod]
    public async Task WhenTheSeedRecordsNameNoSeriesOneBookToSeriesReadOnTheFirstAnchorSuppliesThem()
    {
        var bare = Hit("w1", "Alder") with { Series = [] };
        var rig = new Rig(configure: source =>
        {
            source.SearchHits = _ => [bare, bare with { ExternalId = "w2", Title = "Birch" }];
            source.Claims = () => [new BookSeriesClaim(new SeriesRef("The Saga", SagaSeries), new Claim("1", 1m, null, SagaSeries))];
        });

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga" });

        Assert.AreEqual(SagaSeries, answer.Choices.Single().Reference.Native);
        Assert.AreEqual(1, rig.Source.Reads(DiscoveryOperation.SearchBooks));
        Assert.AreEqual(1, rig.Source.Reads(DiscoveryOperation.FindSeriesForBook), "One anchor, never a fan-out over every hit.");
        Assert.AreEqual(2, answer.Usage.ProviderReads);
        Assert.AreEqual(2, answer.Usage.Attempts, "The seed and the book-to-series read are charged to one allowance.");
        Assert.AreEqual(1, rig.Journeys.Count);
    }

    [TestMethod]
    public async Task ASeedReadsRetryIsChargedAndTheOrdinaryUnbudgetedSearchIsNeverCalled()
    {
        var rig = new Rig(configure: source =>
        {
            source.AttemptsPerRead = 2;
            source.SearchHits = _ => [Hit("w1", "Alder")];
        });

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga" });

        Assert.AreEqual(2, answer.Usage.Attempts);
        Assert.AreEqual(1, answer.Usage.Retries);
        Assert.AreEqual(2, rig.Source.PhysicalAttempts);
    }

    [TestMethod]
    public async Task SavedChoicesComeFirstAndAreOfferedWithoutProviderSpend()
    {
        var rig = new Rig();
        rig.Resolver.Enabled = false;
        rig.Browse.SeriesChoices.Add(new BrowseNameChoice(Guid.NewGuid(), "The Saga", 4, ["Alder", "Birch", "Cedar"]));

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "The Saga" });

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, answer.Outcome);
        Assert.IsNotNull(answer.Choices.Single().Reference.SavedId);
        Assert.AreEqual(4, answer.Choices.Single().KnownCount);
        Assert.AreEqual(0, rig.Source.TotalReads);
    }

    [TestMethod]
    public async Task AuthorNamesYieldChoicesPerNativeIdEvenForALoneNamesakeAndTheOrderOfHitsDecidesNothing()
    {
        var rig = new Rig(configure: source => source.SearchHits = _ =>
        [
            Hit("n1", "Teaching Methods", new BookAuthorCredit("A. Writer", null, "A2")),
            Hit("m1", "A Novel", new BookAuthorCredit("A. Writer", null, "A1")),
            Hit("m2", "Another Novel", new BookAuthorCredit("A. Writer", null, "A1")),
            Hit("o1", "Other Person", new BookAuthorCredit("Someone Else", null, "A3"))
        ]);

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Text = "A. Writer" });

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, answer.Outcome);
        CollectionAssert.AreEquivalent(new[] { "A1", "A2" }, answer.Choices.Select(choice => choice.Reference.Native!.ExternalId).ToArray());
        Assert.AreEqual(2, answer.Choices.Single(choice => choice.Reference.Native!.ExternalId == "A1").KnownCount);
        Assert.AreEqual(1, answer.Choices.Single(choice => choice.Reference.Native!.ExternalId == "A2").KnownCount);
        Assert.AreEqual(0, rig.Source.Reads(DiscoveryOperation.GetAuthorWorks));
    }

    [TestMethod]
    public async Task ASourceThatStatesNoAuthorIdsOffersNoRemoteAuthorChoiceAndSaysWhy()
    {
        var rig = new Rig(configure: source => source.SearchHits = _ => [Hit("g1", "A Book", new BookAuthorCredit("A. Writer", null, null))]);

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Text = "A. Writer" });

        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, answer.Outcome);
        Assert.AreEqual(0, answer.Choices.Count);
        Assert.IsTrue(answer.Coverage.Sources.Any(report => report.ReasonCode == CatalogAnswerCodes.AuthorIdsUnavailable && report.State == CatalogSourceState.Unsupported));
    }

    [TestMethod]
    public async Task ANamedSourceWithNoBudgetedReadIsUnsupportedNotDisabledAndNothingIsCalled()
    {
        var rig = new Rig();
        rig.Resolver.WithPlainSource = true;

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga", ProviderId = "plain" });
        var unknown = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga", ProviderId = "gone" });

        Assert.AreEqual(CatalogAnswerOutcome.Unsupported, answer.Outcome);
        CollectionAssert.Contains(answer.Reasons.ToArray(), CatalogAnswerCodes.NoEligibleSource);
        Assert.AreEqual(CatalogAnswerOutcome.Unavailable, unknown.Outcome);
        CollectionAssert.Contains(unknown.Reasons.ToArray(), "source-removed");
        Assert.AreEqual(0, rig.Source.TotalReads, "No source was asked: choosing one that cannot seed is not a reason to fan out to another.");
    }

    [TestMethod]
    public async Task ASeedSourceFailureIsTypedAndNeverNoChoicesFound()
    {
        var rig = new Rig(configure: source => source.ForcedOutcome = DiscoveryOutcome.TimedOut);

        var answer = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga" });

        Assert.AreEqual(CatalogAnswerOutcome.TimedOut, answer.Outcome);
        Assert.AreEqual(0, answer.Choices.Count);
    }

    [TestMethod]
    public async Task ANamedTitleIsCheckedByOneSeedReadAndOnlyAnExactTitleIsAnAnswer()
    {
        var hit = Hit("w1", "The Winter Garden") with { PublicationDate = new DateOnly(2010, 4, 1), PublicationDateBasis = DateBasis.Original };
        var rig = new Rig(configure: source => source.SearchHits = _ => [hit, Hit("w2", "Winter Garden Cookbook")]);

        var exact = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.PublicationForTitle, Text = "winter garden" });
        var miss = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.PublicationForTitle, Text = "Garden of Spring" });

        Assert.AreEqual("The Winter Garden", exact.Entries.Single().Title);
        Assert.AreEqual(new DateOnly(2010, 4, 1), exact.Entries.Single().Publication.Date);
        Assert.AreEqual(CatalogAnswerOutcome.NoVerifiedMatch, miss.Outcome);
        Assert.AreEqual(0, miss.Entries.Count, "Keyword hits are not relabelled as the answer.");
        Assert.AreEqual(2, rig.Source.Reads(DiscoveryOperation.SearchBooks));
    }

    // ================================================================================================ saved path

    [TestMethod]
    public async Task ASavedSeriesAnswersFromEvidenceWithNoProviderReadNoAdmissionAndSaysItIsLocalOnly()
    {
        var rig = new Rig(limiter: new CatalogExpansionLimiter(maxStarts: 1, TimeSpan.FromMinutes(1)));
        SavedWorkEvidence Saved(string title, DateOnly? date, DatePrecision? precision, DateBasis? basis) =>
            new(Guid.NewGuid(), title, date, precision, basis, PublicationStatus.Published, null, [], false);
        rig.Saved.Series.AddRange(
        [
            new SavedSeriesEntryEvidence(Guid.NewGuid(), 1, "1", true, SeriesPositionState.SingleSource, Saved("Alder", new DateOnly(2001, 1, 1), DatePrecision.Year, DateBasis.Original)),
            new SavedSeriesEntryEvidence(Guid.NewGuid(), 2, "2", true, SeriesPositionState.SingleSource, Saved("Birch", new DateOnly(2005, 1, 1), null, null))
        ]);
        var series = CatalogSubjectRef.Saved(CatalogSubjectKind.Series, "The Saga", Guid.NewGuid());

        var answer = await rig.Ask(Latest(series));
        var again = await rig.Ask(Latest(series));

        Assert.AreEqual("Birch", answer.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, answer.Outcome);
        Assert.IsTrue(answer.Entries.Single().LegacyPrecision);
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.LegacyPrecision));
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.DateBasisUnverified));
        Assert.AreEqual(CatalogSourceState.SavedCatalog, answer.Coverage.Sources.Single().State);
        Assert.AreEqual(0, rig.Source.TotalReads);
        Assert.AreEqual(0, answer.Usage.ProviderReads);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, again.Outcome, "Saved reads need no admission, so the single allowed start is untouched.");
        Assert.AreEqual(2, rig.Saved.Reads);
    }

    [TestMethod]
    public async Task ASavedAuthorMatchesCreditsByIdAndASavedAuthorSeriesQuestionIsUnsupportedNotApproximated()
    {
        var rig = new Rig();
        var authorId = Guid.NewGuid();
        rig.Saved.AuthorWorks.AddRange(
        [
            new SavedWorkEvidence(Guid.NewGuid(), "Mine", new DateOnly(2001, 1, 1), DatePrecision.Day, DateBasis.Original, PublicationStatus.Published, null, [new SavedCreditEvidence(authorId, "A. Writer", null, 0)], false),
            new SavedWorkEvidence(Guid.NewGuid(), "Edited By", new DateOnly(2002, 1, 1), DatePrecision.Day, DateBasis.Original, PublicationStatus.Published, null, [new SavedCreditEvidence(authorId, "A. Writer", "editor", 0)], false),
            new SavedWorkEvidence(Guid.NewGuid(), "Namesake", new DateOnly(2003, 1, 1), DatePrecision.Day, DateBasis.Original, PublicationStatus.Published, null, [new SavedCreditEvidence(Guid.NewGuid(), "A. Writer", null, 0)], false)
        ]);
        var author = CatalogSubjectRef.Saved(CatalogSubjectKind.Author, "A. Writer", authorId);

        var works = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Subject = author });
        var series = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorSeries, Subject = author });

        Assert.AreEqual("Mine", works.Entries.Single().Title);
        Assert.AreEqual("Edited By", works.Groups.Single(group => group.Code == "other-roles").Entries.Single().Title);
        Assert.AreEqual("Namesake", works.Groups.Single(group => group.Code == "credit-unverified").Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Unsupported, series.Outcome);
        Assert.AreEqual(0, rig.Source.TotalReads);
    }

    [TestMethod]
    public async Task ASavedListLongerThanThePageIsCutAndSaysSoNeverExhaustive()
    {
        var rig = new Rig(options: new CatalogAnswerOptions { SavedPageSize = 2 });
        for (var index = 0; index < 3; index++)
        {
            rig.Saved.Series.Add(new SavedSeriesEntryEvidence(Guid.NewGuid(), index + 1, null, true, null,
                new SavedWorkEvidence(Guid.NewGuid(), "B" + index, new DateOnly(2000 + index, 1, 1), DatePrecision.Day, DateBasis.Original, PublicationStatus.Published, null, [], false)));
        }

        var answer = await rig.Ask(Latest(CatalogSubjectRef.Saved(CatalogSubjectKind.Series, "S", Guid.NewGuid())));

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, answer.Outcome);
        Assert.IsTrue(answer.Coverage.Truncated);
        Assert.IsFalse(answer.Coverage.ExhaustedInCheckedScope);
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.KnownLocalOnly));
    }

    // ============================================================================================= contract edges

    [TestMethod]
    public async Task ARequestMissingWhatItsOperationNeedsIsAProgrammingErrorNotAnAnswer()
    {
        var rig = new Rig();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, Subject = Saga() }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AfterAnchor, Subject = Saga() }));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => rig.Ask(new CatalogAnswerRequest { Operation = (CatalogAnswerOperation)99 }));
        Assert.AreEqual(0, rig.Source.TotalReads);
    }

    [TestMethod]
    public async Task AChoicesRootCanBeShownAgainWithNoCallAndASelectionStartsTheRealLookupOnTheSameJourney()
    {
        var rig = new Rig(configure: source => source.SearchHits = _ => [Hit("w1", "Alder")]);
        var asked = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga" });
        var reads = rig.Source.TotalReads;

        var again = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, ContextRootId = asked.Context!.RootId });
        var readsAfterAgain = rig.Source.TotalReads;
        var chosen = rig.Contexts.TryGetChoice(Reader, asked.Context.RootId, 0, rig.Clock.UtcNow)!;
        var selected = await rig.Ask(new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = chosen.Reference, ContextRootId = asked.Context.RootId });

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, again.Outcome);
        Assert.AreEqual(reads, readsAfterAgain, "Showing the choices again makes no call.");
        Assert.AreEqual("Birch", selected.Entries.Single().Title);
        Assert.AreEqual(1, rig.Journeys.Count, "The search, the selection and every later read drew on one allowance.");
        Assert.AreEqual(2, rig.Source.TotalReads);
        Assert.AreEqual(2, rig.Source.PhysicalAttempts);
    }
}
