// One-shot expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Matching, ordering, grouping, paging and qualification rules, independent of storage. The saved-catalog projections
/// themselves are proven against real PostgreSQL in <see cref="CatalogBrowseEndpointTests"/>.
/// </summary>
[TestClass]
public sealed class CatalogBrowseServiceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId { get; } = id;

        public string? DisplayName => "Reader";
    }

    private sealed class FakeRepository : ICatalogBrowseRepository
    {
        public List<BrowseNameChoice> AuthorChoices { get; } = [];

        public List<BrowseNameChoice> SeriesChoices { get; } = [];

        public string? LastAuthorLookup { get; private set; }

        public string? LastSeriesLookup { get; private set; }

        public int Calls { get; private set; }

        public Task<BrowseAuthorHeader?> FindAuthorAsync(Guid authorId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<BrowseAuthorHeader?>(null);
        }

        public Task<BrowseSeriesHeader?> FindSeriesAsync(Guid seriesId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<BrowseSeriesHeader?>(null);
        }

        public Task<IReadOnlyList<BrowseWork>> GetAuthorWorksAsync(Guid authorId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrowseWork>>([]);

        public Task<IReadOnlyList<BrowseSeriesMember>> GetSeriesMembersAsync(Guid seriesId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrowseSeriesMember>>([]);

        public Task<IReadOnlyList<BrowseNameChoice>> FindAuthorsByNormalizedNameAsync(string normalizedName, int take, CancellationToken cancellationToken)
        {
            LastAuthorLookup = normalizedName;
            return Task.FromResult<IReadOnlyList<BrowseNameChoice>>(AuthorChoices.Take(take).ToArray());
        }

        public Task<IReadOnlyList<BrowseNameChoice>> FindSeriesByNormalizedNameAsync(string normalizedName, int take, CancellationToken cancellationToken)
        {
            LastSeriesLookup = normalizedName;
            return Task.FromResult<IReadOnlyList<BrowseNameChoice>>(SeriesChoices.Take(take).ToArray());
        }
    }

    private static CatalogBrowseService Service(FakeRepository? repository = null, Guid? user = null) =>
        new(repository ?? new FakeRepository(), new FakeUser(user ?? Guid.NewGuid()));

    private static ObservedBookRecord Record(
        string external,
        string title,
        string[] authors,
        (string Label, string? Position, decimal? Sort)[]? series = null,
        string provider = "example-source") =>
        new(provider, "Example Source", external, title, null,
            authors.Select((label, ordinal) => new ObservedCredit(ordinal, label, Guid.NewGuid())).ToArray(),
            (series ?? []).Select((item, ordinal) => new ObservedMembership(ordinal, item.Label, item.Position, item.Sort, ordinal == 0, Guid.NewGuid())).ToArray(),
            Observed, false, false);

    private static ObservedSample Sample(
        ObservedSubjectKind kind, string label, string anchor, bool failed = false, bool truncated = false, params ObservedBookRecord[] records) =>
        new(new ObservedSubject(Guid.NewGuid(), kind, "example-source", "Example Source", label, anchor, 0),
            records, failed, truncated, Observed.AddMinutes(10));

    [TestMethod]
    public async Task SeriesPreviewSortsNumericallyKeepsTiesAndUnknownsAndPreservesRawPositionText()
    {
        var sample = Sample(ObservedSubjectKind.Series, "Example Saga", "unknown-1", false, false,
            Record("unknown-1", "Companion Tales", ["Ada Example"], [("Example Saga", "companion", null)]),
            Record("three-b", "Three B", ["Ada Example"], [("Example Saga", "3", 3m)]),
            Record("two-and-a-half", "Between Two", ["Ada Example"], [("Example Saga", "2.5", null)]),
            Record("one", "One", ["Ada Example"], [("Example Saga", "1", 1m)]),
            Record("three-a", "Three A", ["Ada Example"], [("Example Saga", "3", null)]),
            Record("unknown-2", "Another Companion", ["Ada Example"], [("Example Saga", null, null)]));

        var result = await Service().PreviewSeriesAsync(sample, 1, CancellationToken.None);

        Assert.AreEqual(CatalogBrowseStatus.Ok, result.Status);
        CollectionAssert.AreEqual(
            new[] { "one", "two-and-a-half", "three-a", "three-b", "unknown-2", "unknown-1" },
            result.Value!.Entries.Select(entry => entry.Book.ExternalId).ToArray(),
            "1, 2.5, tied 3s by title, then unknowns with a stable tie-break.");
        Assert.AreEqual(2.5m, result.Value.Entries[1].Position);
        Assert.AreEqual("2.5", result.Value.Entries[1].PositionLabel);
        Assert.AreEqual("companion", result.Value.Entries[5].PositionLabel);
        Assert.IsNull(result.Value.Entries[4].Position);
    }

    [TestMethod]
    public async Task OnlyAPlainDecimalLabelBecomesANumber()
    {
        foreach (var (label, expected) in new (string?, decimal?)[]
                 { ("1", 1m), ("2.5", 2.5m), ("0.5", 0.5m), ("1 (publication order)", null), ("1-3", null), ("I", null), ("", null), (null, null), ("1,5", null), ("-1", null) })
        {
            Assert.AreEqual(expected, CatalogBrowseService.ParsePosition(label), $"'{label}'");
        }

        var sample = Sample(ObservedSubjectKind.Series, "Saga", "a", false, false,
            Record("a", "A", ["Ada Example"], [("Saga", "1 (publication order)", null)]));
        var entry = (await Service().PreviewSeriesAsync(sample, 1, CancellationToken.None)).Value!.Entries.Single();
        Assert.IsNull(entry.Position, "A qualified ordering label is not coerced to a number.");
        Assert.AreEqual("1 (publication order)", entry.PositionLabel);
    }

    [TestMethod]
    public async Task OrderingQualifiersInALabelKeepSeriesApartAndDuplicatePositionsAreKept()
    {
        var sample = Sample(ObservedSubjectKind.Series, "Example Saga", "a", false, false,
            Record("a", "Alpha", ["Ada Example"], [("Example Saga", "1", 1m), ("Example Saga (reading order)", "4", 4m)]),
            Record("b", "Beta", ["Ada Example"], [("Example Saga", "1", 1m)]),
            Record("c", "Gamma", ["Ada Example"], [("example saga", "1", 1m)]));

        var series = (await Service().PreviewSeriesAsync(sample, 1, CancellationToken.None)).Value!;

        CollectionAssert.AreEqual(new[] { "a", "b" }, series.Entries.Select(entry => entry.Book.ExternalId).ToArray(),
            "Different labels (other case, other qualifier) are not the selected series; the same position twice is kept.");
        Assert.IsTrue(series.Entries.All(entry => entry.Position == 1m));
        Assert.HasCount(2, series.Entries[0].Book.Memberships, "Multiple memberships survive on the book.");
    }

    [TestMethod]
    public async Task AuthorPreviewMatchesTheFullCreditInOneSourceAndGroupsByFullSeriesLabel()
    {
        var sample = Sample(ObservedSubjectKind.Author, "Ada Example", "a", false, false,
            Record("a", "Alpha", ["Ada Example", "Bo Coauthor"], [("Saga One", "2", 2m)]),
            Record("b", "Beta", ["Ada Example"], [("Saga One", "1", 1m), ("Saga Two", "9", 9m)]),
            Record("c", "Gamma", ["Ada Example"]),
            Record("d", "Delta", ["Ada Examples"], [("Saga One", "3", 3m)]),
            Record("e", "Epsilon", ["ada example"], [("Saga One", "4", 4m)]),
            Record("f", "Foreign", ["Ada Example"], [("Saga One", "5", 5m)], provider: "other-source"));

        var preview = (await Service().PreviewAuthorAsync(sample, 1, CancellationToken.None)).Value!;

        Assert.AreEqual(2, preview.Groups.Count);
        CollectionAssert.AreEqual(new[] { "Saga One", "Saga Two" }, preview.Groups.Select(group => group.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "b", "a" }, preview.Groups[0].Entries.Select(entry => entry.Book.ExternalId).ToArray());
        CollectionAssert.AreEqual(new[] { "b" }, preview.Groups[1].Entries.Select(entry => entry.Book.ExternalId).ToArray(), "A multi-membership book appears in each of its groups.");
        CollectionAssert.AreEqual(new[] { "c" }, preview.Ungrouped.Select(entry => entry.Book.ExternalId).ToArray());
        Assert.IsFalse(preview.Groups.SelectMany(group => group.Entries).Any(entry => entry.Book.ExternalId is "d" or "e" or "f"),
            "Other spellings and other sources are never merged in.");
        Assert.IsNotNull(preview.Groups[0].SubjectId, "A group links to the series subject it was observed as.");
    }

    [TestMethod]
    public async Task EveryPreviewCarriesIdentityAndSampleQualificationsEvenWithExactlyOneSavedNamesake()
    {
        var repository = new FakeRepository();
        repository.AuthorChoices.Add(new BrowseNameChoice(Guid.NewGuid(), "Ada Example", 3, ["A", "B", "C"]));
        var sample = Sample(ObservedSubjectKind.Author, "Ada  Example!", "a", false, false, Record("a", "Alpha", ["Ada  Example!"]));

        var preview = (await Service(repository).PreviewAuthorAsync(sample, 1, CancellationToken.None)).Value!;

        CollectionAssert.IsSubsetOf(
            new[] { CatalogBrowseLimitations.SourceSample, CatalogBrowseLimitations.IdentityUnverified, CatalogBrowseLimitations.GroupingPageLocal },
            preview.Limitations.ToArray());
        Assert.HasCount(1, preview.NameChoices.Choices);
        Assert.IsFalse(preview.NameChoices.Truncated);
        Assert.AreEqual("ada example", repository.LastAuthorLookup, "Saved names are looked up by the catalog's own normalization.");
        Assert.IsTrue(preview.Groups.All(group => group.SeriesId is null), "No saved identity is attributed to the observation.");
    }

    [TestMethod]
    public async Task NameChoicesAreCappedAtTwentyWithATruncationIndicator()
    {
        var repository = new FakeRepository();
        for (var index = 0; index < 25; index++) repository.SeriesChoices.Add(new BrowseNameChoice(Guid.NewGuid(), "Saga", index, []));
        var sample = Sample(ObservedSubjectKind.Series, "Saga", "a", false, false, Record("a", "A", ["X"], [("Saga", "1", 1m)]));

        var preview = (await Service(repository).PreviewSeriesAsync(sample, 1, CancellationToken.None)).Value!;

        Assert.HasCount(20, preview.NameChoices.Choices);
        Assert.IsTrue(preview.NameChoices.Truncated);
        CollectionAssert.Contains(preview.Limitations.ToArray(), CatalogBrowseLimitations.Truncated);
        CollectionAssert.AreEqual(Enumerable.Range(0, 20).ToArray(), preview.NameChoices.Choices.Select(choice => choice.KnownBookCount).ToArray(), "The first twenty, in repository order.");
    }

    [TestMethod]
    public async Task ContextAndRecordTruncationAndSourceFailureAreDisclosed()
    {
        var truncatedRecord = Record("a", "A", ["Ada Example"]) with { CreditsTruncated = true };
        var sample = Sample(ObservedSubjectKind.Author, "Ada Example", "a", failed: true, truncated: false, truncatedRecord);

        var preview = (await Service().PreviewAuthorAsync(sample, 1, CancellationToken.None)).Value!;

        CollectionAssert.IsSubsetOf(
            new[] { CatalogBrowseLimitations.Truncated, CatalogBrowseLimitations.SourceFailed },
            preview.Limitations.ToArray());
    }

    [TestMethod]
    public async Task ObservedPagingUsesFiftyRowsAndOneExtraRowForHasMore()
    {
        var records = Enumerable.Range(0, 120)
            .Select(index => Record($"b{index:000}", $"Title {index:000}", ["Ada Example"], [("Saga", (index + 1).ToString(CultureInfo.InvariantCulture), index + 1)]))
            .ToArray();
        var sample = Sample(ObservedSubjectKind.Series, "Saga", "b000", false, false, records);
        var service = Service();

        var first = (await service.PreviewSeriesAsync(sample, 1, CancellationToken.None)).Value!;
        var second = (await service.PreviewSeriesAsync(sample, 2, CancellationToken.None)).Value!;
        var third = (await service.PreviewSeriesAsync(sample, 3, CancellationToken.None)).Value!;
        var fourth = (await service.PreviewSeriesAsync(sample, 4, CancellationToken.None)).Value!;

        Assert.HasCount(50, first.Entries);
        Assert.IsTrue(first.HasMore);
        Assert.HasCount(50, second.Entries);
        Assert.IsTrue(second.HasMore);
        Assert.HasCount(20, third.Entries);
        Assert.IsFalse(third.HasMore);
        Assert.IsEmpty(fourth.Entries);
        Assert.IsFalse(fourth.HasMore);
        Assert.AreEqual("b050", second.Entries[0].Book.ExternalId, "Pages tile the ordered list without overlap.");

        var exactlyFifty = Sample(ObservedSubjectKind.Series, "Saga", "b000", false, false, records.Take(50).ToArray());
        Assert.IsFalse((await service.PreviewSeriesAsync(exactlyFifty, 1, CancellationToken.None)).Value!.HasMore, "Exactly one page has no more.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(101)]
    public async Task PagesOutsideOneToOneHundredAreInvalidAtEveryEntryPoint(int page)
    {
        var service = Service();
        var sample = Sample(ObservedSubjectKind.Author, "A", "a", false, false, Record("a", "A", ["A"]));

        Assert.AreEqual(CatalogBrowseStatus.InvalidPage, (await service.GetAuthorAsync(Guid.NewGuid(), page, CancellationToken.None)).Status);
        Assert.AreEqual(CatalogBrowseStatus.InvalidPage, (await service.GetSeriesAsync(Guid.NewGuid(), page, CancellationToken.None)).Status);
        Assert.AreEqual(CatalogBrowseStatus.InvalidPage, (await service.PreviewAuthorAsync(sample, page, CancellationToken.None)).Status);
        Assert.AreEqual(CatalogBrowseStatus.InvalidPage, (await service.PreviewSeriesAsync(sample, page, CancellationToken.None)).Status);
        Assert.IsTrue(CatalogBrowseService.IsValidPage(1));
        Assert.IsTrue(CatalogBrowseService.IsValidPage(100));
    }

    [TestMethod]
    public async Task NoSignedInUserFailsClosedBeforeAnyStorageAccess()
    {
        var repository = new FakeRepository();
        var service = new CatalogBrowseService(repository, new FakeUser(null));
        var sample = Sample(ObservedSubjectKind.Author, "A", "a", false, false, Record("a", "A", ["A"]));

        Assert.AreEqual(CatalogBrowseStatus.Unauthenticated, (await service.GetAuthorAsync(Guid.NewGuid(), 1, CancellationToken.None)).Status);
        Assert.AreEqual(CatalogBrowseStatus.Unauthenticated, (await service.GetSeriesAsync(Guid.NewGuid(), 1, CancellationToken.None)).Status);
        Assert.AreEqual(CatalogBrowseStatus.Unauthenticated, (await service.PreviewAuthorAsync(sample, 1, CancellationToken.None)).Status);
        Assert.AreEqual(CatalogBrowseStatus.Unauthenticated, (await service.PreviewSeriesAsync(sample, 1, CancellationToken.None)).Status);
        Assert.AreEqual(0, repository.Calls);
        Assert.IsNull(repository.LastAuthorLookup);
    }

    [TestMethod]
    public async Task MissingSavedIdentityIsNotFoundWhileAnEmptyKnownListSucceeds()
    {
        var result = await Service().GetAuthorAsync(Guid.NewGuid(), 1, CancellationToken.None);
        Assert.AreEqual(CatalogBrowseStatus.NotFound, result.Status);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task KnownFormatsAndVersionReachTheBrowsedObservationAndUnknownsStayUnknown()
    {
        var known = Record("a", "Alpha Omnibus", ["Ada Example"]) with
        {
            Formats = ["Ebook", "Audiobook"],
            VersionKind = "Collection",
            VersionLabel = "Collection or combined volume"
        };
        var unknown = Record("b", "Beta", ["Ada Example"]);
        var sample = Sample(ObservedSubjectKind.Author, "Ada Example", "a", false, false, known, unknown);

        var preview = (await Service().PreviewAuthorAsync(sample, 1, CancellationToken.None)).Value!;

        var collection = preview.Ungrouped.Single(entry => entry.Book.ExternalId == "a").Book;
        CollectionAssert.AreEqual(new[] { "Ebook", "Audiobook" }, collection.Formats.ToArray());
        Assert.AreEqual("Collection", collection.VersionKind);
        Assert.AreEqual("Collection or combined volume", collection.VersionLabel);
        Assert.IsFalse(collection.AssociationsTruncated);
        Assert.IsNull(collection.WorkId, "A format and version label are display facts, not a saved identity.");

        var blank = preview.Ungrouped.Single(entry => entry.Book.ExternalId == "b").Book;
        Assert.IsEmpty(blank.Formats);
        Assert.IsNull(blank.VersionKind);
        Assert.IsNull(blank.VersionLabel);
        Assert.IsNull(blank.PublicationDate);
    }

    [TestMethod]
    public async Task AnOmittedOrTruncatedAssociationIsDisclosedOnTheBrowsedBook()
    {
        var truncated = Record("a", "Alpha", ["Ada Example"]) with { MembershipsTruncated = true, FactsTruncated = true };
        var sample = Sample(ObservedSubjectKind.Author, "Ada Example", "a", false, false, truncated);

        var preview = (await Service().PreviewAuthorAsync(sample, 1, CancellationToken.None)).Value!;

        Assert.IsTrue(preview.Ungrouped.Single().Book.AssociationsTruncated,
            "An empty membership list after truncation is not proof of standalone status.");
        CollectionAssert.Contains(preview.Limitations.ToArray(), CatalogBrowseLimitations.Truncated);
    }

    [TestMethod]
    public async Task APreviewDoesNotInventAuthorshipStandaloneStatusOrCompletion()
    {
        var sample = Sample(ObservedSubjectKind.Author, "Ada Example", "a", false, false, Record("a", "Alpha", ["Ada Example"]));

        var preview = (await Service().PreviewAuthorAsync(sample, 1, CancellationToken.None)).Value!;

        Assert.IsEmpty(preview.Groups);
        var entry = preview.Ungrouped.Single();
        Assert.IsNull(entry.Position);
        Assert.IsNull(entry.Book.PublicationDate);
        Assert.IsTrue(entry.Book.Credits.All(credit => credit.Role is null), "No contributor role is invented.");
        Assert.IsEmpty(entry.Book.Memberships);
        Assert.IsNull(entry.Book.WorkId, "An observation is not a saved Work.");
        Assert.AreEqual("example-source", entry.Book.Sources.Single().ProviderId);
        Assert.AreEqual(Observed, entry.Book.Sources.Single().ObservedAtUtc);
        Assert.AreEqual(Observed, preview.ObservedAtUtc);
    }
}
