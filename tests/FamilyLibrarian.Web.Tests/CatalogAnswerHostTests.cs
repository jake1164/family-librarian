// Expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Claim = FamilyLibrarian.Application.Catalog.Discovery.PositionClaim;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The shared answer service through the real host's production dependency injection and a migrated PostgreSQL database: the
/// saved-evidence projection reads precision, basis, class and credit roles exactly as stored (retired Works excluded), the web
/// discovery core and the answer service draw on one allowance, scoped/singleton lifetimes hold under the host's scope validation,
/// and every journey leaves the catalog, request, follow, notification, evidence and audit tables byte-for-byte unchanged. The
/// source is scripted fictional data; no provider is contacted. The answer service is registered but not yet reachable from chat.
/// </summary>
[TestClass]
public sealed class CatalogAnswerHostTests
{
    private static readonly DateTimeOffset Seeded = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly SourceRef SagaSeries = new("demo", "S1", ExternalReferenceEntityType.Series);
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private sealed class PinnedClock(DateTimeOffset start) : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow => start;

        public override DateTimeOffset GetUtcNow() => start;
    }

    /// <summary>A fictional source with every contract the answer service uses, counting what it is asked.</summary>
    private sealed class HostSource : IBookMetadataProvider, IBookMetadataDiscoveryProvider, IBudgetedBookMetadataProvider
    {
        private readonly ConcurrentDictionary<string, int> reads = new();

        public string Id => "demo";

        public string DisplayName => "Demo Source";

        string IBookMetadataDiscoveryProvider.ProviderId => Id;

        string IBudgetedBookMetadataProvider.ProviderId => Id;

        public DiscoveryCapabilities Capabilities { get; } = new(
            Enum.GetValues<DiscoveryOperation>().Where(operation => operation.IsDiscoveryCapability())
                .Select(operation => new KeyValuePair<DiscoveryOperation, DiscoveryCapability>(operation, DiscoveryCapability.Supported)));

        public int Reads(string name) => reads.TryGetValue(name, out var count) ? count : 0;

        public int TotalReads => reads.Values.Sum();

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException("Unbudgeted search must not be called.");

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unbudgeted detail must not be called.");

        private void Spend(string name, IDiscoveryBudget budget)
        {
            reads.AddOrUpdate(name, 1, (_, count) => count + 1);
            Assert.AreEqual(AttemptAdmission.Admitted, budget.TryAdmitAttempt(isRetry: false), "A read spends its attempt before it answers.");
        }

        public Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            Assert.IsTrue(budget.TryAdmitPage());
            Spend("members", budget);
            SeriesMember Member(string id, string title, int year, decimal position) =>
                new(new DiscoveredBook(new SourceRef("demo", id, ExternalReferenceEntityType.Work), new TitleForm(title), [],
                        PublicationEvidence.Dated(new DateOnly(year, 1, 1), DatePrecision.Year, DateBasis.Original), Seeded),
                    series.Id, [new Claim(position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), position, null, series.Id)]);
            budget.RecordItems(2);
            return Task.FromResult(DiscoveryResult.Found<SeriesMember>(
                [Member("m1", "Member One", 2001, 1), Member("m2", "Member Two", 2004, 2)],
                new DiscoveryScope(DiscoveryOperation.GetSeriesMembers, series.Id), DiscoveryCompletion.ExhaustedInCheckedScope, Seeded, budget.Usage));
        }

        public Task<DiscoveryResult<BookSeriesClaim>> FindSeriesClaimsForBookAsync(SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            Spend("claims", budget);
            return Task.FromResult(DiscoveryResult.Found<BookSeriesClaim>(
                [new BookSeriesClaim(new SeriesRef("The Saga", SagaSeries), new Claim("1", 1m, null, SagaSeries))],
                new DiscoveryScope(DiscoveryOperation.FindSeriesForBook, book), DiscoveryCompletion.ExhaustedInCheckedScope, Seeded, budget.Usage));
        }

        public async Task<BudgetedRead<BookCandidateSearchPage>> SearchBudgetedAsync(BookSearchQuery query, IDiscoveryBudget budget, CancellationToken cancellationToken)
        {
            Assert.IsTrue(budget.TryAdmitPage());
            Spend("search", budget);
            var hit = new BookCandidate("demo", "Demo Source", "m1", "Member One", ["A. Writer"], null, null, null, [], [])
            {
                SourceKind = ExternalReferenceEntityType.Work
            };
            return await Task.FromResult(BudgetedRead.Found(new BookCandidateSearchPage([hit], false), budget.Usage, Seeded));
        }

        public Task<BudgetedRead<BookCandidate>> GetDetailsBudgetedAsync(string externalId, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");

        public Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");

        public Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");

        public Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");

        public Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");
    }

    private static FamilyLibrarianAppFactory Factory(HostSource source) =>
        new(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            services.AddSingleton<IBookMetadataProvider>(source);
            var clock = new PinnedClock(Seeded);
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private static readonly string[] SnapshotTables =
    [
        "catalog.authors", "catalog.works", "catalog.work_authors", "catalog.editions", "catalog.series",
        "catalog.series_entries", "catalog.external_references", "requests.book_requests", "requests.request_participants",
        "requests.request_formats", "requests.request_status_history", "following.follows", "notifications.notification_events",
        "notifications.notification_receipts", "communications.outbound_communications",
        "communications.outbound_communication_deliveries", "audit.audit_events"
    ];

    private static async Task<Dictionary<string, string>> SnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(WebTestFixture.Require(fixture).ConnectionString);
        await connection.OpenAsync();
        var result = new Dictionary<string, string>();
        foreach (var table in SnapshotTables)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }

        return result;
    }

    private sealed record SeededCatalog(Guid SeriesId, Guid AuthorId, Guid NamesakeId, Guid IllustratedWorkId, string AuthorName);

    private static async Task<SeededCatalog> SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var authorName = "Writer " + Guid.NewGuid().ToString("N")[..10];
        var writer = new Author(authorName, null, Seeded);
        var namesake = new Author(authorName, null, Seeded);
        var series = new Series("The Saga", SeriesStatus.Unknown, Seeded);
        database.Authors.AddRange(writer, namesake);
        database.Series.Add(series);

        Work NewWork(string title, DateOnly? date, DatePrecision? precision, DateBasis? basis, string? versionClass = null, bool retire = false)
        {
            var work = new Work(title, null, null, date, PublicationStatus.Published, Seeded);
            if (precision is not null || basis is not null) work.SetFirstPublication(date, precision, basis, PublicationStatus.Published, Seeded);
            if (versionClass is not null) work.SetVersionClass(versionClass);
            if (retire) work.Retire(Seeded);
            database.Works.Add(work);
            return work;
        }

        var alder = NewWork("Alder", new DateOnly(2001, 5, 1), DatePrecision.Day, DateBasis.Original);
        var birch = NewWork("Birch", new DateOnly(2005, 1, 1), DatePrecision.Year, DateBasis.Original);
        var cedar = NewWork("Cedar", new DateOnly(2006, 3, 3), null, null);
        var omnibus = NewWork("The Omnibus", new DateOnly(2020, 2, 2), DatePrecision.Day, DateBasis.Original, "Collection");
        var retired = NewWork("Retired", new DateOnly(2030, 1, 1), DatePrecision.Day, DateBasis.Original, retire: true);
        alder.AddAuthor(writer, 0);
        birch.AddAuthor(writer, 0);
        cedar.AddAuthor(writer, 0, "illustrator");
        omnibus.AddAuthor(writer, 0);
        retired.AddAuthor(writer, 0);
        var other = NewWork("Namesake Book", new DateOnly(2002, 2, 2), DatePrecision.Day, DateBasis.Original);
        other.AddAuthor(namesake, 0);
        var position = 1m;
        foreach (var work in new[] { alder, birch, cedar, omnibus, retired })
        {
            var entry = new SeriesEntry(series, work, position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), position, true, Seeded);
            entry.ApplyPosition(entry.PositionLabel, position, SeriesPositionState.SingleSource, Seeded);
            database.SeriesEntries.Add(entry);
            position++;
        }

        await database.SaveChangesAsync();
        return new SeededCatalog(series.Id, writer.Id, namesake.Id, cedar.Id, authorName);
    }

    // ================================================================================================== projection

    [TestMethod]
    public async Task TheSavedProjectionReadsPrecisionBasisClassAndRolesAsStoredAndExcludesRetiredWorks()
    {
        await using var factory = Factory(new HostSource());
        var seeded = await SeedAsync(factory.Services);
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISavedCatalogEvidenceRepository>();

        var rows = await repository.GetSeriesEvidenceAsync(seeded.SeriesId, 0, 50, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "Alder", "Birch", "Cedar", "The Omnibus" }, rows.Select(row => row.Work.Title).ToArray(), "Ordered by position; the retired Work is excluded.");
        var alder = rows[0].Work;
        Assert.AreEqual(DatePrecision.Day, alder.Precision);
        Assert.AreEqual(DateBasis.Original, alder.Basis);
        Assert.AreEqual(DatePrecision.Year, rows[1].Work.Precision);
        Assert.IsNull(rows[2].Work.Precision, "A legacy row stays legacy: no precision is invented.");
        Assert.IsNull(rows[2].Work.Basis);
        Assert.AreEqual("Collection", rows[3].Work.VersionClass);
        Assert.AreEqual(1m, rows[0].PositionSort);
        Assert.AreEqual(SeriesPositionState.SingleSource, rows[0].PositionState);
        Assert.AreEqual("illustrator", rows[2].Work.Credits.Single().Role);
        Assert.AreEqual(seeded.AuthorId, rows[2].Work.Credits.Single().AuthorId);
        Assert.IsFalse(rows.Any(row => row.Work.CreditsTruncated));

        var page = await repository.GetSeriesEvidenceAsync(seeded.SeriesId, 1, 2, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Birch", "Cedar" }, page.Select(row => row.Work.Title).ToArray(), "Skip and take are applied in the database.");
    }

    [TestMethod]
    public async Task ASavedSeriesAnswersFromRealRowsWithoutPromotingLegacyOrCollectionEvidence()
    {
        var source = new HostSource();
        await using var factory = Factory(source);
        var seeded = await SeedAsync(factory.Services);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>();
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "session-a");
        var series = CatalogSubjectRef.Saved(CatalogSubjectKind.Series, "The Saga", seeded.SeriesId);

        var latest = await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = series }, CancellationToken.None);
        var position = await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, Subject = series, Position = 4 }, CancellationToken.None);

        // The omnibus (2020) is a collection, not an original; the legacy row is read as its whole year with no basis, so it is shown but qualified.
        Assert.AreEqual("Cedar", latest.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, latest.Outcome);
        CollectionAssert.IsSubsetOf(
            new[] { CatalogAnswerCodes.LegacyPrecision, CatalogAnswerCodes.DateBasisUnverified, CatalogAnswerCodes.KnownLocalOnly },
            latest.Qualifiers.ToArray());
        Assert.AreEqual("The Omnibus", latest.Groups.Single(group => group.Code == "excluded-by-class").Entries.Single().Title);
        Assert.AreEqual("The Omnibus", position.Entries.Single().Title);
        Assert.IsTrue(position.HasQualifier(CatalogAnswerCodes.ClassNotOriginal));
        Assert.AreEqual(0, source.TotalReads, "Saved evidence needs no provider.");
        Assert.AreEqual(0, latest.Usage.ProviderReads);
    }

    [TestMethod]
    public async Task ASavedAuthorSeparatesRolesAndANamesakeByRealIds()
    {
        await using var factory = Factory(new HostSource());
        var seeded = await SeedAsync(factory.Services);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>();
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Matrix, "!room:example");

        var works = await service.AnswerAsync(caller, new CatalogAnswerRequest
        {
            Operation = CatalogAnswerOperation.AuthorWorks,
            Subject = CatalogSubjectRef.Saved(CatalogSubjectKind.Author, "A. Writer", seeded.AuthorId)
        }, CancellationToken.None);
        var namesake = await service.AnswerAsync(caller, new CatalogAnswerRequest
        {
            Operation = CatalogAnswerOperation.AuthorWorks,
            Subject = CatalogSubjectRef.Saved(CatalogSubjectKind.Author, "A. Writer", seeded.NamesakeId)
        }, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "Alder", "Birch", "The Omnibus" }, works.Entries.Select(entry => entry.Title).ToArray());
        Assert.AreEqual("Cedar", works.Groups.Single(group => group.Code == "other-roles").Entries.Single().Title, "An illustrator credit is not authorship.");
        Assert.AreEqual("Namesake Book", namesake.Entries.Single().Title);
        Assert.IsTrue(works.HasQualifier(CatalogAnswerCodes.ListNotExhaustive));
    }

    [TestMethod]
    public async Task LatestPublishedForASavedAuthorUsesRealCreditsRolesClassAndDatesWithNoProviderRead()
    {
        var source = new HostSource();
        await using var factory = Factory(source);
        var seeded = await SeedAsync(factory.Services);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>();
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "session-a");
        var author = CatalogSubjectRef.Saved(CatalogSubjectKind.Author, seeded.AuthorName, seeded.AuthorId);

        var latest = await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = author }, CancellationToken.None);
        var series = await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorSeries, ContextRootId = latest.Context!.RootId }, CancellationToken.None);

        // Birch (2005, year, original) is the newest credited original: the illustrated Cedar is another role, the omnibus a collection,
        // and the retired Work is gone.
        Assert.AreEqual("Birch", latest.Entries.Single().Title);
        Assert.AreEqual(CatalogAnswerOutcome.Qualified, latest.Outcome);
        CollectionAssert.IsSubsetOf(new[] { CatalogAnswerCodes.KnownLocalOnly, CatalogAnswerCodes.ListNotExhaustive }, latest.Qualifiers.ToArray());
        Assert.AreEqual("Cedar", latest.Groups.Single(group => group.Code == "other-roles").Entries.Single().Title);
        Assert.AreEqual("The Omnibus", latest.Groups.Single(group => group.Code == "excluded-by-class").Entries.Single().Title);
        Assert.AreEqual(0, source.TotalReads);
        // A saved works window is no evidence about the author's series: the follow-up is unsupported for saved data, never "none found".
        Assert.AreEqual(CatalogAnswerOutcome.Unsupported, series.Outcome);
    }

    [TestMethod]
    public async Task ASavedNameProducesSavedChoicesFromTheRealCatalogAndNeverSelects()
    {
        await using var factory = Factory(new HostSource());
        var seeded = await SeedAsync(factory.Services);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>();
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "s");

        var answer = await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AuthorWorks, Text = seeded.AuthorName.ToLowerInvariant() }, CancellationToken.None);

        Assert.AreEqual(CatalogAnswerOutcome.Clarification, answer.Outcome);
        CollectionAssert.IsSubsetOf(new[] { seeded.AuthorId, seeded.NamesakeId }, answer.Choices.Where(choice => choice.Reference.SavedId is not null).Select(choice => choice.Reference.SavedId!.Value).ToArray());
        Assert.AreEqual(0, answer.Entries.Count);
    }

    // =========================================================================== production dependency injection

    [TestMethod]
    public async Task TheWebCoreAndTheAnswerServiceDrawOnOneAllowanceInTheProductionContainer()
    {
        var source = new HostSource();
        await using var factory = Factory(source);
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "session-a");
        var limiter = factory.Services.GetRequiredService<CatalogExpansionLimiter>();
        var series = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Saga", SagaSeries);

        // The web discovery routes use this same singleton; a lookup running there holds the user's one slot here too.
        Assert.AreEqual(ExpansionAdmission.Admitted, limiter.TryStart(caller.OwnerId, Seeded, out var webLookup, out _));
        CatalogAnswer refused;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            refused = await scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>().AnswerAsync(
                caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = series }, CancellationToken.None);
        }

        var readsWhileRefused = source.TotalReads;
        webLookup!.Dispose();
        CatalogAnswer allowed;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            allowed = await scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>().AnswerAsync(
                caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = series }, CancellationToken.None);
        }

        Assert.AreEqual(CatalogAnswerOutcome.Refused, refused.Outcome);
        Assert.AreEqual(0, readsWhileRefused, "The refusal made no provider call.");
        Assert.AreEqual(CatalogAnswerOutcome.Answered, allowed.Outcome);
        Assert.AreEqual("Member Two", allowed.Entries.Single().Title);
        Assert.AreEqual(1, source.Reads("members"));
    }

    [TestMethod]
    public async Task LifetimesHoldTheServiceIsScopedItsStateIsSharedAndTheHostsScopeValidationAcceptsIt()
    {
        await using var factory = Factory(new HostSource());
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();

        Assert.AreNotSame(first.ServiceProvider.GetRequiredService<ICatalogAnswerService>(), second.ServiceProvider.GetRequiredService<ICatalogAnswerService>());
        Assert.AreNotSame(first.ServiceProvider.GetRequiredService<ISavedCatalogEvidenceRepository>(), second.ServiceProvider.GetRequiredService<ISavedCatalogEvidenceRepository>());
        Assert.AreSame(first.ServiceProvider.GetRequiredService<CatalogAnswerContextStore>(), second.ServiceProvider.GetRequiredService<CatalogAnswerContextStore>());
        Assert.AreSame(first.ServiceProvider.GetRequiredService<CatalogDiscoveryJourneys>(), second.ServiceProvider.GetRequiredService<CatalogDiscoveryJourneys>());
        Assert.AreSame(first.ServiceProvider.GetRequiredService<CatalogBrowseContextStore>(), second.ServiceProvider.GetRequiredService<CatalogBrowseContextStore>());
        Assert.AreSame(factory.Services.GetRequiredService<CatalogExpansionLimiter>(), first.ServiceProvider.GetRequiredService<CatalogExpansionLimiter>());
    }

    [TestMethod]
    public async Task ARootStartedInOneScopeServesAFollowUpInAnotherForTheSameCallerOnly()
    {
        var source = new HostSource();
        await using var factory = Factory(source);
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Matrix, "!room-a:example");
        CatalogAnswer first;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>().AnswerAsync(
                caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Saga", SagaSeries) }, CancellationToken.None);
        }

        // A fresh scope per message, as Matrix execution does: the follow-up is served from the retained root with no provider call.
        CatalogAnswer followUp;
        CatalogAnswer otherRoom;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>();
            var request = new CatalogAnswerRequest { Operation = CatalogAnswerOperation.Position, ContextRootId = first.Context!.RootId, Position = 1 };
            followUp = await service.AnswerAsync(caller, request, CancellationToken.None);
            otherRoom = await service.AnswerAsync(new CatalogAnswerCaller(caller.OwnerId, CatalogAnswerTransport.Matrix, "!room-b:example"), request, CancellationToken.None);
        }

        Assert.AreEqual("Member One", followUp.Entries.Single().Title);
        Assert.AreEqual(0, followUp.Usage.ProviderReads);
        Assert.AreEqual(CatalogAnswerOutcome.ContextExpired, otherRoom.Outcome);
        Assert.AreEqual(1, source.TotalReads);
    }

    // =============================================================================================== no durable write

    [TestMethod]
    public async Task EveryAnswerJourneyLeavesEveryDurableTableUnchangedAndMakesOnlyBudgetedReads()
    {
        var source = new HostSource();
        await using var factory = Factory(source);
        var seeded = await SeedAsync(factory.Services);
        var before = await SnapshotAsync();
        var caller = new CatalogAnswerCaller(Guid.NewGuid(), CatalogAnswerTransport.Web, "session-a");
        var series = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Saga", SagaSeries);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICatalogAnswerService>();
            var remote = await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = series }, CancellationToken.None);
            await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.AfterAnchor, ContextRootId = remote.Context!.RootId, Position = 1 }, CancellationToken.None);
            await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.NextRelease, ContextRootId = remote.Context.RootId }, CancellationToken.None);
            await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Subject = CatalogSubjectRef.Saved(CatalogSubjectKind.Series, "The Saga", seeded.SeriesId) }, CancellationToken.None);
            await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.LatestPublished, Text = "the saga" }, CancellationToken.None);
            await service.AnswerAsync(caller, new CatalogAnswerRequest { Operation = CatalogAnswerOperation.PublicationForTitle, Text = "Member One" }, CancellationToken.None);
        }

        var after = await SnapshotAsync();
        foreach (var table in SnapshotTables) Assert.AreEqual(before[table], after[table], table);
        Assert.AreEqual(1, source.Reads("members"));
        Assert.AreEqual(2, source.Reads("search"));
        Assert.AreEqual(1, source.Reads("claims"));
    }
}
