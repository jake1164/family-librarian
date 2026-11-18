using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The host's explicit-lookup service over the real context store, journey ledger and per-user limiter, with a scripted
/// provider that counts every discovery call. No network, database or HTTP: this proves the ordering of the checks (a
/// rejected request never reaches the provider), the shared allowance across pages, the absolute expiry and the context bounds.
/// The scripted provider's data is hand-built; none of it is a provider response.
/// </summary>
[TestClass]
public sealed class CatalogDiscoveryServiceTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Stranger = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static BookCandidate Candidate(string externalId = "b1", string title = "Book One") =>
        new("demo", "Demo Source", externalId, title, ["Frank Herbert", "Unanchored Person"], null, null, null, [],
            [new BookSeriesCandidate("Saga", "1", true) { SourceSeriesId = "S1" }, new BookSeriesCandidate("Loose Label", "2", false)])
        {
            AuthorCredits = [new BookAuthorCredit("Frank Herbert", null, "A1")],
            SourceKind = ExternalReferenceEntityType.Work
        };

    private sealed class TestClock : TimeProvider, IClock
    {
        private long ticks = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class Resolver(ScriptedDiscoveryProvider provider) : IActiveMetadataProviderResolver
    {
        public bool Enabled { get; set; } = true;

        public bool Registered { get; set; } = true;

        /// <summary>Runs while the provider is being resolved, the way a slow settings read lets time pass.</summary>
        public Action? WhileResolving { get; set; }

        public Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IBookMetadataProvider>>(Enabled ? [provider] : []);

        public Task<IBookMetadataProvider?> FindActiveProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            WhileResolving?.Invoke();
            return Task.FromResult<IBookMetadataProvider?>(Enabled && providerId == provider.Id ? provider : null);
        }

        public bool IsRegistered(string providerId) => Registered && providerId == provider.Id;
    }

    private sealed class Rig
    {
        public Rig(CatalogBrowseContextLimits? limits = null, CatalogExpansionLimiter? limiter = null, ScriptedDiscoveryProvider? provider = null)
        {
            Provider = provider ?? new ScriptedDiscoveryProvider(books: [Candidate()]);
            Resolver = new Resolver(Provider);
            Store = new CatalogBrowseContextStore(limits ?? CatalogBrowseContextLimits.Default);
            Journeys = new CatalogDiscoveryJourneys();
            Limiter = limiter ?? new CatalogExpansionLimiter();
            Service = new CatalogDiscoveryService(Store, Journeys, Limiter, Resolver, Clock, Clock);
        }

        public TestClock Clock { get; } = new();

        public ScriptedDiscoveryProvider Provider { get; }

        public Resolver Resolver { get; }

        public CatalogBrowseContextStore Store { get; }

        public CatalogDiscoveryJourneys Journeys { get; }

        public CatalogExpansionLimiter Limiter { get; }

        public CatalogDiscoveryService Service { get; }

        /// <summary>An ordinary search observation for <see cref="Owner"/>: the root context every lookup starts from.</summary>
        public (Guid ContextId, ObservedBookRecord Record) Observe(Guid? owner = null, BookCandidate? candidate = null)
        {
            var book = candidate ?? Candidate();
            var lease = Store.Observe(owner ?? Owner, "run:" + Guid.NewGuid().ToString("N"),
                [new BrowseObservation("demo", "Demo Source", true, [book])], Clock.UtcNow)!;
            return (lease.ContextId, Store.TryGetRecord(owner ?? Owner, lease.ContextId, "demo", book.ExternalId, Clock.UtcNow)!.Record);
        }
    }

    // ================================================================ what an observed context offers

    [TestMethod]
    public async Task EachKindOfObservedSubjectOffersOnlyTheLookupsItsSourceProvenanceSupports()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();

        var book = await rig.Service.OffersAsync(Owner, context, record.BookSubjectId!.Value, CancellationToken.None);
        var namedSeries = await rig.Service.OffersAsync(Owner, context, record.Memberships[0].SubjectId, CancellationToken.None);
        var looseSeries = await rig.Service.OffersAsync(Owner, context, record.Memberships[1].SubjectId, CancellationToken.None);
        var knownAuthor = await rig.Service.OffersAsync(Owner, context, record.Credits[0].SubjectId, CancellationToken.None);
        var nameOnlyAuthor = await rig.Service.OffersAsync(Owner, context, record.Credits[1].SubjectId, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { ExpansionKind.BookSeries }, book.Offers.Select(offer => offer.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { ExpansionKind.SeriesMembers }, namedSeries.Offers.Select(offer => offer.Kind).ToArray());
        Assert.IsEmpty(looseSeries.Offers, "A series known only by its label has no native id to look up, and a label is never promoted.");
        CollectionAssert.AreEqual(new[] { ExpansionKind.AuthorWorks, ExpansionKind.AuthorSeries }, knownAuthor.Offers.Select(offer => offer.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { ExpansionKind.ResolveAuthor }, nameOnlyAuthor.Offers.Select(offer => offer.Kind).ToArray(),
            "A name with no source id may only be put to the source with the book that credited it, and returns choices.");
        Assert.IsTrue(book.Offers.All(offer => offer.CanStart));
        Assert.AreEqual(0, rig.Provider.DiscoveryCalls, "Offering is a settings read; it never contacts a provider.");
    }

    [TestMethod]
    public async Task OffersStateCapabilityApartFromAnyResultAndNeverPromoteUnverifiedOrUnsupported()
    {
        var rig = new Rig();
        rig.Provider.Capabilities = new DiscoveryCapabilities(
        [
            new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Unverified("live-probe-not-run")),
            new(DiscoveryOperation.GetSeriesMembers, DiscoveryCapability.Unsupported("series-endpoints-not-key-accessible"))
        ]);
        var (context, record) = rig.Observe();

        var book = (await rig.Service.OffersAsync(Owner, context, record.BookSubjectId!.Value, CancellationToken.None)).Offers.Single();
        var series = (await rig.Service.OffersAsync(Owner, context, record.Memberships[0].SubjectId, CancellationToken.None)).Offers.Single();
        var author = await rig.Service.OffersAsync(Owner, context, record.Credits[0].SubjectId, CancellationToken.None);

        Assert.AreEqual("Unverified", book.Capability);
        Assert.IsTrue(book.CanStart, "Unverified may be attempted, and is labelled as such.");
        Assert.AreEqual("Unsupported", series.Capability);
        Assert.IsFalse(series.CanStart, "An unsupported lookup has no actionable start.");
        Assert.AreEqual("series-endpoints-not-key-accessible", series.ReasonCode);
        Assert.IsTrue(author.Offers.All(offer => offer is { CanStart: true, Capability: "Unverified", ReasonCode: "not-declared" }),
            "A capability that was never declared is Unverified, never Supported.");
    }

    [TestMethod]
    public async Task ADisabledOrRemovedSourceOffersNothingActionable()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();
        rig.Resolver.Enabled = false;

        var disabled = (await rig.Service.OffersAsync(Owner, context, record.BookSubjectId!.Value, CancellationToken.None)).Offers.Single();
        rig.Resolver.Registered = false;
        var removed = (await rig.Service.OffersAsync(Owner, context, record.BookSubjectId!.Value, CancellationToken.None)).Offers.Single();

        Assert.IsFalse(disabled.CanStart);
        Assert.AreEqual("source-disabled", disabled.ReasonCode);
        Assert.AreEqual("source-removed", removed.ReasonCode);
        Assert.AreEqual(0, rig.Provider.DiscoveryCalls);
    }

    [TestMethod]
    public async Task ARecordWhoseSourceKindIsUnknownGetsNoBookSubjectAndNoLookup()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe(candidate: new BookCandidate(
            "demo", "Demo Source", "k1", "Kindless", ["Someone"], null, null, null, [], []));

        Assert.IsNull(record.BookSubjectId);
        var author = await rig.Service.OffersAsync(Owner, context, record.Credits[0].SubjectId, CancellationToken.None);
        Assert.IsEmpty(author.Offers, "Without the adapter saying what its id identifies, no anchor can be formed.");
    }

    // ================================================================ rejections make no provider call and spend no start

    [TestMethod]
    public async Task EveryRejectionBeforeTheLimiterMakesNoProviderCallAndSpendsNoStart()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();
        var (_, strangerRecord) = rig.Observe(Stranger);
        var series = record.Memberships[0].SubjectId;

        // foreign owner, unknown context, unknown subject, wrong scope, expired
        var foreign = await rig.Service.ExpandAsync(Stranger, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);
        var unknownContext = await rig.Service.ExpandAsync(Owner, Guid.NewGuid(), record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);
        var unknownSubject = await rig.Service.ExpandAsync(Owner, context, Guid.NewGuid(), ExpansionKind.BookSeries, CancellationToken.None);
        var crossContext = await rig.Service.ExpandAsync(Owner, context, strangerRecord.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);
        var wrongScope = await rig.Service.ExpandAsync(Owner, context, series, ExpansionKind.AuthorWorks, CancellationToken.None);
        rig.Resolver.Enabled = false;
        var disabled = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None);
        rig.Resolver.Enabled = true;
        rig.Provider.Capabilities = new DiscoveryCapabilities([new(DiscoveryOperation.FindSeriesForBook, DiscoveryCapability.Unsupported("not-offered"))]);
        var unsupported = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        var expired = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(ExpansionStatus.NotFound, foreign.Status, "Another owner's context answers like an unknown one.");
        Assert.AreEqual(ExpansionStatus.NotFound, unknownContext.Status);
        Assert.AreEqual(ExpansionStatus.NotFound, unknownSubject.Status);
        Assert.AreEqual(ExpansionStatus.NotFound, crossContext.Status, "A subject from one context cannot be spent in another.");
        Assert.AreEqual(ExpansionStatus.BadScope, wrongScope.Status);
        Assert.AreEqual(ExpansionStatus.SourceUnavailable, disabled.Status);
        Assert.AreEqual(ExpansionStatus.Unsupported, unsupported.Status);
        Assert.AreEqual(ExpansionStatus.NotFound, expired.Status);
        Assert.AreEqual(0, rig.Provider.DiscoveryCalls);

        // No start was spent: a full window is still available to this owner.
        rig.Provider.Capabilities = new DiscoveryCapabilities(
            Enum.GetValues<DiscoveryOperation>().Select(operation => new KeyValuePair<DiscoveryOperation, DiscoveryCapability>(operation, DiscoveryCapability.Supported)));
        var (fresh, freshRecord) = rig.Observe();
        for (var i = 0; i < CatalogExpansionLimiter.DefaultMaxStarts; i++)
        {
            var ok = await rig.Service.ExpandAsync(Owner, fresh, freshRecord.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);
            Assert.AreEqual(ExpansionStatus.Ok, ok.Status, $"start {i + 1}");
        }
    }

    // ================================================================ N1: expiry is rechecked after the awaited resolution

    [TestMethod]
    public async Task AContextThatExpiresWhileTheSourceIsBeingResolvedMakesNoProviderCallAndSpendsNoStart()
    {
        var rig = new Rig(new CatalogBrowseContextLimits { Lifetime = TimeSpan.FromSeconds(1) });
        var (context, record) = rig.Observe();
        rig.Resolver.WhileResolving = () => rig.Clock.Advance(TimeSpan.FromSeconds(2));

        var result = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(ExpansionStatus.NotFound, result.Status, "The context ended during the settings read, so it is not a subject any more.");
        Assert.AreEqual(0, rig.Provider.DiscoveryCalls, "No adapter call, so no wire call, for an expired context.");
        Assert.AreEqual(0, rig.Journeys.Count, "No journey was created from the stale instant.");
        Assert.IsNull(result.Window);

        // No start was spent: the whole window is still available to this owner.
        rig.Resolver.WhileResolving = null;
        var limiter = rig.Limiter;
        for (var i = 0; i < limiter.MaxStarts; i++)
        {
            Assert.AreEqual(ExpansionAdmission.Admitted, limiter.TryStart(Owner, rig.Clock.UtcNow, out var permit, out _), $"start {i + 1}");
            permit!.Dispose();
        }
    }

    [TestMethod]
    public async Task AWindowThatExpiresWhileASourceIsBeingResolvedCannotBeContinuedOrExpandedAgain()
    {
        var rig = new Rig(new CatalogBrowseContextLimits { Lifetime = TimeSpan.FromSeconds(30) });
        var (context, record) = rig.Observe();
        var window = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;
        var calls = rig.Provider.DiscoveryCalls;
        rig.Clock.Advance(TimeSpan.FromSeconds(29));

        rig.Resolver.WhileResolving = () => rig.Clock.Advance(TimeSpan.FromSeconds(2));
        var continued = await rig.Service.ContinueAsync(Owner, window.ContextId, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromSeconds(-2));
        rig.Resolver.WhileResolving = null;

        Assert.AreEqual(ExpansionStatus.NotFound, continued.Status);
        Assert.AreEqual(calls, rig.Provider.DiscoveryCalls, "An expired window makes no new provider call.");
        Assert.AreEqual(ExpansionAdmission.Admitted, rig.Limiter.TryStart(Owner, rig.Clock.UtcNow, out var permit, out _));
        permit!.Dispose();
    }

    [TestMethod]
    public async Task AJourneyIsCreatedAgainstTheOriginalRootInstantNotRefreshedByTheTimeALookupTook()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();
        var rootExpiry = rig.Clock.UtcNow + TimeSpan.FromMinutes(10);
        // The settings read takes four minutes: the journey must still end at the root's instant, not four minutes later.
        rig.Resolver.WhileResolving = () => rig.Clock.Advance(TimeSpan.FromMinutes(4));

        var result = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(rootExpiry, result.Window!.ExpiresAtUtc);
        var journey = rig.Journeys.Find(Owner, result.Window.Header.JourneyId)!;
        Assert.AreEqual(rootExpiry, journey.ExpiresAtUtc, "Journey life is measured against the original absolute instant.");
    }

    [TestMethod]
    public async Task CancellationBeforeDispatchStillPropagatesAndSpendsNoWindow()
    {
        var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        var (context, record) = rig.Observe();
        rig.Resolver.WhileResolving = () => cancel.Cancel();
        var before = rig.Store.Statistics().Contexts;

        try
        {
            await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // The permitted outcome for a caller who cancelled; it is never converted into an answer.
        }

        Assert.AreEqual(before, rig.Store.Statistics().Contexts);
    }

    // ================================================================ N3: stated contributor roles

    [TestMethod]
    public async Task AStatedContributorRoleSurvivesFromTheSharedBookIntoTheWindowAndANullRoleStaysNull()
    {
        var rig = new Rig();
        rig.Provider.WorksScript = (author, budget) => DiscoveryResult.Found<AuthorWork>(
            [
                new AuthorWork(ScriptedDiscoveryProvider.Book("w1", "Painted Only", new CreditRef(0, "Pat Painter", "illustrator", null)), []),
                new AuthorWork(ScriptedDiscoveryProvider.Book(
                    "w2", "Mixed", new CreditRef(0, "Frank Herbert", null, author.Id), new CreditRef(1, "Tess Translator", "translator", null)), [])
            ],
            new DiscoveryScope(DiscoveryOperation.GetAuthorWorks, author.Id), DiscoveryCompletion.ExhaustedInCheckedScope,
            DateTimeOffset.UnixEpoch, budget.Usage);
        var (context, record) = rig.Observe();

        var window = (await rig.Service.ExpandAsync(Owner, context, record.Credits[0].SubjectId, ExpansionKind.AuthorWorks, CancellationToken.None)).Window!;

        var painted = window.Records[0].Credits.Single();
        Assert.AreEqual("Pat Painter", painted.Label);
        Assert.AreEqual("illustrator", painted.Role, "The sole credit is an illustrator, and the window says so.");
        Assert.IsNull(painted.NativeAuthorId);
        var mixed = window.Records[1].Credits;
        Assert.IsNull(mixed[0].Role, "A credit the source gave no role keeps no role; it is not rewritten to author.");
        Assert.AreEqual("A1", mixed[0].NativeAuthorId);
        Assert.AreEqual("translator", mixed[1].Role);
        CollectionAssert.AreEqual((int[])[0, 1], mixed.Select(credit => credit.Ordinal).ToArray());
        Assert.AreEqual(2, mixed.Select(credit => credit.SubjectId).Distinct().Count(), "Each credit keeps its own subject.");
    }

    // ================================================================ per-user window and one running lookup

    [TestMethod]
    public async Task TheEleventhStartInAWindowIsRefusedWithoutAProviderCallAndTheWindowReopensLater()
    {
        // Ten windows would evict the search sample under the default ten-context bound, which is a different rule under test elsewhere.
        var rig = new Rig(new CatalogBrowseContextLimits { MaxContextsPerOwner = 50, MaxContextsTotal = 200 });
        var (context, record) = rig.Observe();
        var subject = record.BookSubjectId!.Value;

        for (var i = 0; i < 10; i++)
        {
            Assert.AreEqual(ExpansionStatus.Ok, (await rig.Service.ExpandAsync(Owner, context, subject, ExpansionKind.BookSeries, CancellationToken.None)).Status);
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        var callsBefore = rig.Provider.DiscoveryCalls;
        var refused = await rig.Service.ExpandAsync(Owner, context, subject, ExpansionKind.BookSeries, CancellationToken.None);
        Assert.AreEqual(callsBefore, rig.Provider.DiscoveryCalls, "The refused start reached no provider.");
        var otherUser = rig.Observe(Stranger);
        var neighbour = await rig.Service.ExpandAsync(Stranger, otherUser.ContextId, otherUser.Record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(ExpansionStatus.RateLimited, refused.Status);
        Assert.AreEqual(TimeSpan.FromSeconds(40), refused.RetryAfter, "The refusal says how long until the fixed window ends.");
        Assert.AreEqual(ExpansionStatus.Ok, neighbour.Status, "The window is per signed-in user.");

        rig.Clock.Advance(TimeSpan.FromSeconds(41));
        Assert.AreEqual(ExpansionStatus.Ok, (await rig.Service.ExpandAsync(Owner, context, subject, ExpansionKind.BookSeries, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task ASecondLookupWhileOneIsRunningIsRefusedNotQueuedAndTheSlotIsReleasedAfterwards()
    {
        var rig = new Rig();
        rig.Provider.Gate = new SemaphoreSlim(0);
        var (context, record) = rig.Observe();
        var subject = record.BookSubjectId!.Value;

        var first = rig.Service.ExpandAsync(Owner, context, subject, ExpansionKind.BookSeries, CancellationToken.None);
        await rig.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await rig.Service.ExpandAsync(Owner, context, subject, ExpansionKind.BookSeries, CancellationToken.None);
        rig.Provider.Gate.Release();
        var completed = await first;
        rig.Provider.Gate = null;
        var third = await rig.Service.ExpandAsync(Owner, context, subject, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(ExpansionStatus.Busy, second.Status);
        Assert.AreEqual(2, rig.Provider.Calls(DiscoveryOperation.FindSeriesForBook),
            "Only the running lookup and the later one reached the provider; the refused one did not queue behind them.");
        Assert.AreEqual(ExpansionStatus.Ok, completed.Status);
        Assert.AreEqual(ExpansionStatus.Ok, third.Status);
    }

    [TestMethod]
    public async Task CancellationStopsTheLookupAdmitsNoWindowAndFreesTheSlot()
    {
        var rig = new Rig();
        rig.Provider.Gate = new SemaphoreSlim(0);
        using var cancel = new CancellationTokenSource();
        var (context, record) = rig.Observe();
        var before = rig.Store.Statistics().Contexts;

        var running = rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, cancel.Token);
        await rig.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);

        Assert.AreEqual(before, rig.Store.Statistics().Contexts, "A cancelled lookup leaves no window behind.");
        rig.Provider.Gate = null;
        Assert.AreEqual(ExpansionStatus.Ok,
            (await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None)).Status);
    }

    // ================================================================ the lookups and what they keep

    [TestMethod]
    public async Task ABookLookupKeepsEachSeriesClaimWithItsPositionAndAnUnnamedSeriesStaysUnnamed()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();

        var result = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);

        var window = result.Window!;
        Assert.AreEqual("Ok", window.Header.Outcome);
        Assert.AreEqual("FindSeriesForBook", window.Header.Operation);
        Assert.AreEqual("Supported", window.Header.CapabilityState);
        Assert.AreEqual(1, rig.Provider.Calls(DiscoveryOperation.FindSeriesForBook), "One lookup, one provider read.");
        Assert.AreEqual(1, window.Header.Attempts);
        var book = window.Records.Single();
        Assert.AreEqual(ExternalReferenceEntityType.Work, book.SourceKind);
        Assert.AreEqual("Book One", book.Title, "The book keeps the title it was already shown under.");
        Assert.HasCount(2, book.Memberships);
        Assert.AreEqual("Saga", book.Memberships[0].Label);
        Assert.AreEqual("1", book.Memberships[0].PositionLabel);
        Assert.AreEqual("S1", book.Memberships[0].NativeSeriesId);
        Assert.IsTrue(book.Memberships[1].NameMissing);
        Assert.AreEqual(string.Empty, book.Memberships[1].Label, "No name is invented for an unnamed series.");
        Assert.AreEqual("S2", book.Memberships[1].NativeSeriesId);
        Assert.AreEqual("native", book.Memberships[0].Relationship);
        // The window's own series subjects can be looked up again (native ids), the unnamed one included.
        var unnamed = await rig.Service.OffersAsync(Owner, window.ContextId, book.Memberships[1].SubjectId, CancellationToken.None);
        Assert.AreEqual(ExpansionKind.SeriesMembers, unnamed.Offers.Single().Kind);
    }

    [TestMethod]
    public async Task AWindowNeverOutlivesAndNeverRefreshesTheRootContextsAbsoluteExpiry()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();
        var rootExpiry = rig.Clock.UtcNow + TimeSpan.FromMinutes(10);
        rig.Clock.Advance(TimeSpan.FromMinutes(6));

        var result = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);
        var members = await rig.Service.ExpandAsync(Owner, result.Window!.ContextId, result.Window.Records[0].Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None);

        Assert.AreEqual(rootExpiry, result.Window.ExpiresAtUtc, "The window inherits the original expiry.");
        Assert.AreEqual(rootExpiry, members.Window!.ExpiresAtUtc, "A window of a window does too.");
        rig.Clock.Advance(TimeSpan.FromMinutes(4));
        var after = await rig.Service.ExpandAsync(Owner, members.Window.ContextId, members.Window.Records[0].BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);
        Assert.AreEqual(ExpansionStatus.NotFound, after.Status, "Nothing extends the life of the context the subject came from.");
        Assert.IsNull(rig.Store.TryGetWindow(Owner, result.Window.ContextId, rig.Clock.UtcNow));
    }

    [TestMethod]
    public async Task ContinuationFollowsTheSameJourneyAllowanceAndIsServerIssuedAndOwnerBound()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();

        var first = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;
        Assert.AreEqual("MoreAvailable", first.Header.Completion);
        Assert.IsNotNull(first.Header.ContinuationToken);
        Assert.HasCount(2, first.Records);

        var foreign = await rig.Service.ContinueAsync(Stranger, first.ContextId, CancellationToken.None);
        var unknown = await rig.Service.ContinueAsync(Owner, Guid.NewGuid(), CancellationToken.None);
        Assert.AreEqual(ExpansionStatus.NotFound, foreign.Status);
        Assert.AreEqual(ExpansionStatus.NotFound, unknown.Status);
        Assert.AreEqual(1, rig.Provider.Calls(DiscoveryOperation.GetSeriesMembers), "Refused continuations reached no provider.");

        var second = (await rig.Service.ContinueAsync(Owner, first.ContextId, CancellationToken.None)).Window!;
        Assert.AreEqual("ExhaustedInCheckedScope", second.Header.Completion);
        Assert.IsNull(second.Header.ContinuationToken);
        Assert.AreEqual("Member Three", second.Records.Single().Title);
        Assert.AreEqual(first.Header.JourneyId, second.Header.JourneyId, "Both pages spend one journey.");
        Assert.AreEqual(2, second.Header.Pages, "The page ceiling is counted across pages, not reset by a re-entry.");
        Assert.AreEqual(2, second.Header.Attempts, "So are the physical attempts.");
        Assert.AreEqual(first.Header.ParentContextId, second.Header.ParentContextId, "The next page keeps the original starting point.");

        var done = await rig.Service.ContinueAsync(Owner, second.ContextId, CancellationToken.None);
        Assert.AreEqual(ExpansionStatus.NoContinuation, done.Status);
    }

    [TestMethod]
    public async Task AReplayedContinuationSpendsTheSameAllowanceAndTwoJourneysStayIndependent()
    {
        var rig = new Rig(new CatalogBrowseContextLimits { MaxContextsPerOwner = 50, MaxContextsTotal = 200 });
        var (contextA, recordA) = rig.Observe();
        var (contextB, recordB) = rig.Observe();

        var first = (await rig.Service.ExpandAsync(Owner, contextA, recordA.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;
        var replayOne = (await rig.Service.ContinueAsync(Owner, first.ContextId, CancellationToken.None)).Window!;
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var replayTwo = (await rig.Service.ContinueAsync(Owner, first.ContextId, CancellationToken.None)).Window!;
        var other = (await rig.Service.ExpandAsync(Owner, contextB, recordB.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;

        Assert.AreEqual(2, replayOne.Header.Pages, "A re-entry is another page of the same operation.");
        Assert.AreEqual(3, replayTwo.Header.Pages, "A replay of the same token does not reset the allowance: it spends the next page.");
        Assert.AreEqual(3, replayTwo.Header.Attempts);
        Assert.AreNotEqual(first.Header.JourneyId, other.Header.JourneyId, "A different root context is a different journey.");
        Assert.AreEqual(1, other.Header.Pages, "The second journey starts with its own allowance.");
        Assert.AreEqual(2, rig.Journeys.Count);
    }

    [TestMethod]
    public void TheJourneyLedgerIsOwnerBoundBoundedAndExpiresWithItsRoot()
    {
        var clock = new TestClock();
        var journeys = new CatalogDiscoveryJourneys();
        var root = Guid.NewGuid();
        var expires = clock.UtcNow + TimeSpan.FromMinutes(10);

        var journey = journeys.GetOrCreateForRoot(Owner, root, expires, clock.UtcNow, clock)!;
        Assert.AreSame(journey, journeys.GetOrCreateForRoot(Owner, root, expires, clock.UtcNow, clock), "One root, one journey.");
        Assert.AreSame(journey, journeys.Find(Owner, journey.JourneyId));
        Assert.IsNull(journeys.Find(Stranger, journey.JourneyId), "Another owner cannot reach it.");
        Assert.IsNull(journeys.GetOrCreateForRoot(Owner, Guid.NewGuid(), clock.UtcNow, clock.UtcNow, clock), "A root with no life left starts no journey.");

        for (var i = 0; i < CatalogDiscoveryJourneys.MaxJourneysPerOwner + 3; i++)
        {
            journeys.GetOrCreateForRoot(Owner, Guid.NewGuid(), expires, clock.UtcNow, clock);
        }

        Assert.AreEqual(CatalogDiscoveryJourneys.MaxJourneysPerOwner, journeys.Count, "The per-owner bound evicts the oldest first.");
        Assert.IsNull(journeys.Find(Owner, journey.JourneyId), "The oldest journey was evicted.");
        clock.Advance(TimeSpan.FromMinutes(11));
        var fresh = journeys.GetOrCreateForRoot(Owner, Guid.NewGuid(), clock.UtcNow + TimeSpan.FromMinutes(1), clock.UtcNow, clock);
        Assert.IsNotNull(fresh);
        Assert.AreEqual(1, journeys.Count, "Expired journeys are pruned.");
    }

    [TestMethod]
    public async Task ThePageCeilingIsSharedAcrossContinuationsSoEndlessPagingStops()
    {
        var rig = new Rig();
        rig.Provider.MembersScript = (cursor, budget) => DiscoveryResult.Found<SeriesMember>(
            [new SeriesMember(ScriptedDiscoveryProvider.Book("m" + Guid.NewGuid().ToString("N"), "Member"), ScriptedDiscoveryProvider.SagaSeries, [])],
            new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, ScriptedDiscoveryProvider.SagaSeries),
            DiscoveryCompletion.MoreAvailable, DateTimeOffset.UnixEpoch, budget.Usage,
            new DiscoveryCursor("p:" + Guid.NewGuid().ToString("N")));
        var (context, record) = rig.Observe();

        var window = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;
        var pages = 1;
        while (window.Header.ContinuationToken is not null && pages < 20)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(7));
            var next = await rig.Service.ContinueAsync(Owner, window.ContextId, CancellationToken.None);
            if (next.Window is null) break;
            window = next.Window;
            pages++;
        }

        Assert.AreEqual(6, pages, "Five pages is the operation ceiling; the sixth request is a limit result with no continuation.");
        Assert.AreEqual("Failed", window.Header.Outcome);
        CollectionAssert.Contains(window.Header.Notes.ToArray(), DiscoveryCodes.PageCeiling);
        Assert.IsNull(window.Header.ContinuationToken);
        Assert.AreEqual(5, rig.Provider.Calls(DiscoveryOperation.GetSeriesMembers), "No provider read happens past the ceiling.");
    }

    [TestMethod]
    public async Task AuthorLookupsChainFromANameToChoicesToWorksAndDerivedSeries()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();

        var choices = (await rig.Service.ExpandAsync(Owner, context, record.Credits[1].SubjectId, ExpansionKind.ResolveAuthor, CancellationToken.None)).Window!;
        Assert.AreEqual("Ambiguous", choices.Header.Outcome);
        Assert.HasCount(2, choices.Refs, "Two exact-name people stay two choices; none is selected.");
        Assert.AreEqual("8 October 1920 – 11 February 1986", choices.Refs[0].Disambiguation);
        Assert.AreEqual(141, choices.Refs[0].WorkCount);

        var works = (await rig.Service.ExpandAsync(Owner, choices.ContextId, choices.Refs[0].SubjectId, ExpansionKind.AuthorWorks, CancellationToken.None)).Window!;
        var series = (await rig.Service.ExpandAsync(Owner, choices.ContextId, choices.Refs[0].SubjectId, ExpansionKind.AuthorSeries, CancellationToken.None)).Window!;

        Assert.AreEqual("Work One", works.Records.Single().Title);
        Assert.AreEqual(new DateOnly(1965, 1, 1), works.Records.Single().PublicationDate);
        Assert.AreEqual(DatePrecision.Year, works.Records.Single().PublicationDatePrecision);
        Assert.AreEqual(DateBasis.Original, works.Records.Single().PublicationDateBasis);
        var derived = series.Refs.Single();
        Assert.AreEqual("derived:author-works-checked-pages", derived.Relationship, "A derived author-to-series edge is labelled as derived.");
        Assert.AreEqual("S1", derived.NativeId);
    }

    [TestMethod]
    public async Task ProviderStatesSurviveIntoTheWindowAndAPartialOrEmptyAnswerIsNeverDressedUp()
    {
        var rig = new Rig();
        rig.Provider.MembersScript = (_, budget) => DiscoveryResult.PartialSuccess<SeriesMember>(
            [new SeriesMember(ScriptedDiscoveryProvider.Book("m1", "Kept"), ScriptedDiscoveryProvider.SagaSeries, [])],
            new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, ScriptedDiscoveryProvider.SagaSeries),
            DateTimeOffset.UnixEpoch, budget.Usage, [DiscoveryCodes.PartialFieldError]);
        var (context, record) = rig.Observe();
        var partial = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;

        rig.Provider.MembersScript = (_, budget) => DiscoveryResult.NoUsableItems<SeriesMember>(
            new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, ScriptedDiscoveryProvider.SagaSeries),
            DiscoveryCompletion.ExhaustedInCheckedScope, null, DateTimeOffset.UnixEpoch, budget.Usage, [DiscoveryCodes.ItemMissingId]);
        var unusable = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;

        rig.Provider.MembersScript = (_, budget) => DiscoveryResult.NotAnswered<SeriesMember>(
            DiscoveryOutcome.Throttled, new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, ScriptedDiscoveryProvider.SagaSeries),
            DateTimeOffset.UnixEpoch, budget.Usage, [DiscoveryCodes.RetryAfterTooLong]);
        var throttled = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;

        Assert.AreEqual("Partial", partial.Header.Outcome);
        Assert.AreEqual("ProviderFailedPartial", partial.Header.Completion);
        Assert.AreEqual("Kept", partial.Records.Single().Title);
        Assert.AreEqual("NoUsableItems", unusable.Header.Outcome);
        Assert.IsEmpty(unusable.Records);
        Assert.AreEqual("Throttled", throttled.Header.Outcome);
        CollectionAssert.Contains(throttled.Header.Notes.ToArray(), DiscoveryCodes.RetryAfterTooLong);
        Assert.IsNull(throttled.Header.ContinuationToken);
    }

    // ================================================================ context bounds

    [TestMethod]
    public async Task ARecordBoundCutIsStatedWithNoExhaustionClaimAndTheContinuationIsWithheld()
    {
        var rig = new Rig(new CatalogBrowseContextLimits { MaxRecordsPerContext = 1 });
        var (context, record) = rig.Observe();

        var window = (await rig.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;

        Assert.AreEqual(2, window.Header.ItemsOffered);
        Assert.AreEqual(1, window.Header.ItemsRetained);
        Assert.AreEqual("ContextCapacity", window.Header.Completion, "Not exhausted, and not MoreAvailable: items were cut.");
        CollectionAssert.Contains(window.Header.Notes.ToArray(), "context-capacity");
        Assert.IsNull(window.Header.ContinuationToken, "Following it would skip the cut item.");
        Assert.IsTrue(window.Truncated);
        Assert.AreEqual("Member One", window.Records.Single().Title, "The kept items are a contiguous prefix.");
    }

    [TestMethod]
    public async Task TheSerializedByteBudgetBoundsAWindowAndStatesTheCut()
    {
        var wide = new Rig();
        var (wideContext, wideRecord) = wide.Observe();
        var full = (await wide.Service.ExpandAsync(Owner, wideContext, wideRecord.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;
        var snapshot = wide.Store.TryGetSnapshot(Owner, full.ContextId, wide.Clock.UtcNow)!;
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, WebJson).Length;
        Assert.AreEqual(2, full.Header.ItemsRetained, "With the default budget both members are kept.");

        var tight = new Rig(new CatalogBrowseContextLimits { MaxBytesPerContext = size - 50 });
        var (context, record) = tight.Observe();
        var cut = (await tight.Service.ExpandAsync(Owner, context, record.Memberships[0].SubjectId, ExpansionKind.SeriesMembers, CancellationToken.None)).Window!;

        Assert.AreEqual(2, cut.Header.ItemsOffered);
        Assert.AreEqual(1, cut.Header.ItemsRetained, "The budget is the real serialized size, so the second member does not fit.");
        Assert.AreEqual("ContextCapacity", cut.Header.Completion);
        Assert.IsNull(cut.Header.ContinuationToken);
        var kept = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            tight.Store.TryGetSnapshot(Owner, cut.ContextId, tight.Clock.UtcNow)!, WebJson).Length;
        Assert.IsLessThanOrEqualTo(size - 50, kept, "What is retained never exceeds the budget.");
    }

    [TestMethod]
    public void AHeaderThatCannotFitIsRefusedAndLeavesNoContextBehind()
    {
        var store = new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxBytesPerContext = 1_000 });
        var header = new ObservedDiscovery(
            Guid.NewGuid(), "GetSeriesMembers", "demo", "Demo Source", "Ok", "ExhaustedInCheckedScope",
            Enumerable.Repeat("a-stable-note-code", 80).ToArray(), "Supported", null, Guid.NewGuid(), Guid.NewGuid(),
            ObservedSubjectKind.Series, "Saga", 1, 0, 1, 0, 0, DateTimeOffset.UnixEpoch);
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        var lease = store.AdmitDiscoveryWindow(Owner, new DiscoveryWindowInput(header, "Demo Source", [], []), now.AddMinutes(5), now);

        Assert.IsNull(lease);
        Assert.AreEqual(0, store.Statistics().Contexts);
        Assert.IsNull(store.AdmitDiscoveryWindow(Owner, new DiscoveryWindowInput(header with { Notes = [] }, "Demo Source", [], []), now, now),
            "A window whose expiry has already passed is never admitted.");
    }

    [TestMethod]
    public async Task WindowsCountAgainstTheSameOwnerAndGlobalContextBoundsAndEvictOldestFirst()
    {
        var rig = new Rig(new CatalogBrowseContextLimits { MaxContextsPerOwner = 3, MaxContextsTotal = 4 });
        var (context, record) = rig.Observe();
        var first = (await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None)).Window!;
        await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None);
        await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(3, rig.Store.ContextCountFor(Owner), "The owner bound is shared with ordinary contexts.");
        Assert.IsNotNull(rig.Store.TryGetWindow(Owner, first.ContextId, rig.Clock.UtcNow), "A newer window survives.");
        var evicted = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId.Value, ExpansionKind.BookSeries, CancellationToken.None);
        Assert.AreEqual(ExpansionStatus.NotFound, evicted.Status, "The oldest context (the search sample) was evicted and is honestly gone.");

        rig.Observe(Stranger);
        rig.Observe(Stranger);
        rig.Observe(Stranger);
        Assert.IsLessThanOrEqualTo(4, rig.Store.Statistics().Contexts);
    }

    [TestMethod]
    public async Task AWindowIsNeverServedByTheOrdinaryLabelMatchingPreviews()
    {
        var rig = new Rig();
        var (context, record) = rig.Observe();
        var window = (await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None)).Window!;
        var unnamed = window.Records[0].Memberships[1].SubjectId;

        Assert.IsNull(rig.Store.TryGetSample(Owner, window.ContextId, ObservedSubjectKind.Series, unnamed, rig.Clock.UtcNow),
            "An unnamed series would match every other unnamed series by its empty label.");
        Assert.IsNull(rig.Store.TryGetWindow(Owner, context, rig.Clock.UtcNow), "An ordinary search context is not a lookup window.");
    }

    // ================================================================ ledger bounds

    [TestMethod]
    public async Task TheJourneyOperationLimitEndsFurtherLookupsWithNoProviderCall()
    {
        var rig = new Rig(
            new CatalogBrowseContextLimits { MaxContextsPerOwner = 50, MaxContextsTotal = 200 },
            new CatalogExpansionLimiter(1000, TimeSpan.FromSeconds(60)));
        var (context, record) = rig.Observe();
        for (var i = 0; i < DiscoveryLimits.Default.MaxOperationsPerJourney; i++)
        {
            Assert.AreEqual(ExpansionStatus.Ok, (await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None)).Status);
        }

        var calls = rig.Provider.DiscoveryCalls;
        var refused = await rig.Service.ExpandAsync(Owner, context, record.BookSubjectId!.Value, ExpansionKind.BookSeries, CancellationToken.None);

        Assert.AreEqual(ExpansionStatus.JourneyEnded, refused.Status);
        Assert.AreEqual(calls, rig.Provider.DiscoveryCalls);
    }

    [TestMethod]
    public void TheLimiterIsAFixedWindowWithNoQueueAndFreesItsSlotOnDispose()
    {
        var limiter = new CatalogExpansionLimiter(2, TimeSpan.FromSeconds(60));
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.AreEqual(ExpansionAdmission.Admitted, limiter.TryStart(Owner, now, out var first, out _));
        Assert.AreEqual(ExpansionAdmission.Busy, limiter.TryStart(Owner, now, out var none, out _));
        Assert.IsNull(none);
        first!.Dispose();
        first.Dispose();
        Assert.AreEqual(ExpansionAdmission.Admitted, limiter.TryStart(Owner, now.AddSeconds(1), out var second, out _));
        second!.Dispose();
        Assert.AreEqual(ExpansionAdmission.RateLimited, limiter.TryStart(Owner, now.AddSeconds(30), out _, out var retry));
        Assert.AreEqual(TimeSpan.FromSeconds(30), retry);
        Assert.AreEqual(ExpansionAdmission.Admitted, limiter.TryStart(Owner, now.AddSeconds(60), out var again, out _));
        again!.Dispose();
    }
}
