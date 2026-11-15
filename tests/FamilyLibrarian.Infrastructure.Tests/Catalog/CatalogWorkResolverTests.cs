using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class CatalogWorkResolverTests
{
    [TestMethod]
    public async Task ResolveFillsCatalogPageCountOnCreateAndOnAnIsbnReuseWithoutOverwriting()
    {
        foreach (var prior in new int?[] { null, 200, 320 })
        {
            var repository = new InMemoryCatalogRepository();
            var existing = prior is null ? null : new Work("Project Hail Mary", null, null, null, PublicationStatus.Unknown, Now);
            if (existing is not null)
            {
                existing.AddEdition(new Edition(existing.Id, "Project Hail Mary", EditionFormat.Ebook, "9780593135204", null, Now));
                if (prior == 320) existing.SetCatalogLength(320, null, "first-source", Now);
                repository.AddWork(existing);
            }
            var provider = new StubProvider(CreateCandidate() with { PageCount = 200 });
            var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());
            var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);
            Assert.AreEqual(prior == 320 ? 320 : 200, result.Work.CatalogPageCount);
            Assert.AreEqual(prior == 320 ? "first-source" : "stub", result.Work.CatalogLengthSource);
            Assert.IsNull(result.Work.CatalogAudioDurationSeconds);
        }
    }

    [TestMethod]
    public void CatalogLengthOnlyFillsUnknownValuesAndDoesNotChangeAnEmptyMeasurement()
    {
        var work = new Work("Test Work", null, null, null, PublicationStatus.Unknown, Now);
        work.SetCatalogLength(null, null, "example-source", Now.AddDays(1));
        Assert.IsNull(work.CatalogLengthSource);
        Assert.AreEqual(Now, work.UpdatedAtUtc);
        work.SetCatalogLength(320, null, "example-source", Now.AddDays(1));
        work.SetCatalogLength(100, 72000, "example-library", Now.AddDays(2));
        Assert.AreEqual(320, work.CatalogPageCount);
        Assert.AreEqual(72000, work.CatalogAudioDurationSeconds);
        Assert.AreEqual("example-source", work.CatalogLengthSource);
    }
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void GroupMatchingCandidatesKeepsOnlyTheMostCompleteCandidate()
    {
        var sparse = CreateCandidate() with
        {
            ProviderId = "source-a",
            Description = null,
            Series = []
        };
        var complete = CreateCandidate() with { ProviderId = "source-b" };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([sparse, complete]);

        Assert.HasCount(1, grouped);
        Assert.AreEqual("source-b", grouped[0].ProviderId);
    }

    [TestMethod]
    public void GroupMatchingCandidatesMergesDifferentPrintEditionsOfTheSameWork()
    {
        // Same work from two providers with different ISBNs -- one for the
        // hardcover, one for the paperback -- neither of which matters for
        // acquiring an ebook/audiobook. They should still merge into one row.
        var hardcover = CreateCandidate() with
        {
            ProviderId = "source-a",
            Editions = [new BookEditionCandidate("Project Hail Mary", "9780593135204", "Hardcover", new DateOnly(2021, 5, 4))]
        };
        var paperback = CreateCandidate() with
        {
            ProviderId = "source-b",
            Editions = [new BookEditionCandidate("Project Hail Mary", "9780593396166", "Paperback", new DateOnly(2022, 4, 26))]
        };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([hardcover, paperback]);

        Assert.HasCount(1, grouped);
    }

    [TestMethod]
    public void GroupMatchingCandidatesRecordsEveryMergedProviderAsASource()
    {
        // The UI needs to link out to every source a merged record came from,
        // not just the one chosen to represent the group -- see MergedSources.
        var hardcover = CreateCandidate() with
        {
            ProviderId = "source-a",
            SourceUrl = "https://source-a.example/work",
            Editions = [new BookEditionCandidate("Project Hail Mary", "9780593135204", "Hardcover", new DateOnly(2021, 5, 4))]
        };
        var paperback = CreateCandidate() with
        {
            ProviderId = "source-b",
            SourceUrl = "https://source-b.example/work",
            Editions = [new BookEditionCandidate("Project Hail Mary", "9780593396166", "Paperback", new DateOnly(2022, 4, 26))]
        };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([hardcover, paperback]);

        Assert.HasCount(1, grouped);
        var mergedProviderIds = grouped[0].MergedSources
            .Select(source => source.ProviderId)
            .OrderBy(providerId => providerId, StringComparer.Ordinal)
            .ToArray();
        Assert.HasCount(2, mergedProviderIds);
        Assert.AreEqual("source-a", mergedProviderIds[0]);
        Assert.AreEqual("source-b", mergedProviderIds[1]);
    }

    [TestMethod]
    public void GroupMatchingCandidatesMergesOnNormalizedLastFirstAuthorName()
    {
        // One provider (e.g. Hardcover) gives "Last, First" with a trailing note;
        // another gives the plain "First Last" form. Both describe the same person
        // and the same work, so they should merge.
        var plainName = CreateCandidate("Terminal List", "plain-name") with
        {
            ProviderId = "source-a",
            Authors = ["Jack Carr"],
            Editions = []
        };
        var lastFirstWithNote = CreateCandidate("Terminal List", "last-first-note") with
        {
            ProviderId = "source-b",
            Authors = ["Carr, Jack (Joint pseudonym)"],
            Editions = []
        };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([plainName, lastFirstWithNote]);

        Assert.HasCount(1, grouped);
    }

    [TestMethod]
    public void GroupMatchingCandidatesKeepsDifferentLanguageEditionsSeparate()
    {
        var english = CreateCandidate("Little Women", "english") with
        {
            ProviderId = "source-a",
            Authors = ["Louisa May Alcott"],
            Editions = [],
            Language = "en"
        };
        var spanish = CreateCandidate("Little Women", "spanish") with
        {
            ProviderId = "source-b",
            Authors = ["Louisa May Alcott"],
            Editions = [],
            Language = "es"
        };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([english, spanish]);

        Assert.HasCount(2, grouped);
    }

    [TestMethod]
    public void GroupMatchingCandidatesMergesTitlesDifferingOnlyByALeadingArticle()
    {
        // Real observed live-testing gap: one provider's "Gray Man" and another's
        // "The Gray Man", same author, same book -- must merge into one row.
        var withoutArticle = CreateCandidate("Gray Man", "no-article") with
        {
            ProviderId = "source-a",
            Authors = ["Mark Greaney"],
            Editions = []
        };
        var withArticle = CreateCandidate("The Gray Man", "with-article") with
        {
            ProviderId = "source-b",
            Authors = ["Mark Greaney"],
            Editions = []
        };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([withoutArticle, withArticle]);

        Assert.HasCount(1, grouped);
    }

    [TestMethod]
    public void GroupMatchingCandidatesRanksExactTitleMatchesAheadOfBroadProviderMatches()
    {
        var results = BookCandidateGrouper.GroupMatchingCandidates(
            [
                CreateCandidate("Dim sum of all fears", "dim-sum") with { Editions = [] },
                CreateCandidate("Kol ha-peḥadim kulam", "translated") with { Editions = [] },
                CreateCandidate("Sea of Islands", "sea") with { Editions = [] },
                CreateCandidate("The Sum of All Fears", "sum-of-all-fears") with { Editions = [] }
            ],
            "sum of all fears");

        Assert.HasCount(4, results);
        Assert.AreEqual("The Sum of All Fears", results[0].Title);
    }

    [TestMethod]
    public void GroupMatchingCandidatesRanksPreferredLanguageAheadOfOtherLanguagesOnTiedMatchKind()
    {
        // Titles are chosen so that alphabetical order alone (the tiebreak below
        // the language rank) would put the Spanish edition first; only the
        // language tiebreak should push it behind the English and unknown-language
        // candidates.
        var spanish = CreateCandidate("Amenaza inminente", "spanish-edition") with
        {
            Editions = [],
            Language = "es"
        };
        var english = CreateCandidate("Clear and Present Danger", "english-edition") with
        {
            Editions = [],
            Language = "en"
        };
        var unknownLanguage = CreateCandidate("Bystander", "unknown-language") with
        {
            Editions = [],
            Language = null
        };

        var results = BookCandidateGrouper.GroupMatchingCandidates(
            [spanish, english, unknownLanguage],
            "tom clancy");

        Assert.HasCount(3, results);
        Assert.AreEqual("unknown-language", results[0].ExternalId);
        Assert.AreEqual("english-edition", results[1].ExternalId);
        Assert.AreEqual("spanish-edition", results[2].ExternalId);
    }

    [TestMethod]
    public void GroupMatchingCandidatesRanksASubstringMatchAheadOfAnUnrelatedResult()
    {
        // RANK-1: a genuine substring match (title contains the whole search
        // phrase, just not at the start) used to rank no higher than a
        // completely unrelated result -- both fell into the same undifferentiated
        // "Other" tier. It now gets its own tier between "Close" and "Other".
        var results = BookCandidateGrouper.GroupMatchingCandidates(
            [
                CreateCandidate("American Film, Volume VII, Number 9", "unrelated") with { Editions = [] },
                CreateCandidate("The Return of Bad Luck and Trouble", "contains-match") with { Editions = [] }
            ],
            "bad luck and trouble");

        Assert.HasCount(2, results);
        Assert.AreEqual("contains-match", results[0].ExternalId);
    }

    [TestMethod]
    public void GroupMatchingCandidatesRanksATypoedSearchAheadOfUnrelatedResultsByTokenOverlap()
    {
        // RANK-1, reproducing the exact live-observed failure: searching a
        // typo ("back luck and trouble" for "Bad Luck and Trouble") shares no
        // substring with the intended title at all -- "back" != "bad" -- so it
        // still lands in the "Other" tier alongside every unrelated result and
        // used to fall back to alphabetical order. Token overlap (3 of the 4
        // search words -- "luck", "and", "trouble" -- appear in the title)
        // should still rank it ahead of results that share none of those words.
        var results = BookCandidateGrouper.GroupMatchingCandidates(
            [
                CreateCandidate("American Film, Volume VII, Number 9", "american-film") with { Editions = [] },
                CreateCandidate("Sam \"Lightnin'\" Hopkins", "sam-hopkins") with { Editions = [] },
                CreateCandidate("Bad Luck and Trouble", "bad-luck-and-trouble") with { Editions = [] }
            ],
            "back luck and trouble");

        Assert.HasCount(3, results);
        Assert.AreEqual("bad-luck-and-trouble", results[0].ExternalId);
    }

    [TestMethod]
    public async Task ResolveAsyncNotifiesEveryFollowerOfAMatchedSeriesAndAuthor()
    {
        var repository = new InMemoryCatalogRepository();
        var existingSeries = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now);
        repository.AddSeries(existingSeries);
        var existingAuthor = new Author("Andy Weir", null, Now);
        repository.AddAuthor(existingAuthor);

        var follows = new InMemoryFollowRepository();
        var seriesFollowerId = Guid.NewGuid();
        var authorFollowerId = Guid.NewGuid();
        follows.Seed(new Follow(seriesFollowerId, FollowSubjectType.Series, existingSeries.Id, Now));
        follows.Seed(new Follow(authorFollowerId, FollowSubjectType.Author, existingAuthor.Id, Now));

        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());

        var provider = new StubProvider(CreateCandidate());
        var resolver = CreateResolver(provider, repository, follows, notifications);

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.HasCount(2, notificationRepository.Added);
        var seriesNotification = notificationRepository.Added.Single(
            notification => notification.Category == NotificationCategories.SeriesNewEntryDetected);
        Assert.AreEqual(seriesFollowerId, seriesNotification.RecipientUserId);
        Assert.AreEqual(NotificationSubjectTypes.Work, seriesNotification.SubjectType);
        Assert.AreEqual(result.Work.Id.ToString(), seriesNotification.SubjectId);
        StringAssert.Contains(seriesNotification.Title, "Project Hail Mary");

        var authorNotification = notificationRepository.Added.Single(
            notification => notification.Category == NotificationCategories.AuthorNewWorkDetected);
        Assert.AreEqual(authorFollowerId, authorNotification.RecipientUserId);
        StringAssert.Contains(authorNotification.Title, "Project Hail Mary");
    }

    [TestMethod]
    public async Task ResolveAsyncDoesNotNotifyForABrandNewSeriesOrAuthorNobodyCouldHaveFollowedYet()
    {
        var repository = new InMemoryCatalogRepository();
        var follows = new InMemoryFollowRepository();
        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());
        var provider = new StubProvider(CreateCandidate());
        var resolver = CreateResolver(provider, repository, follows, notifications);

        await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        // Neither the series nor the author existed before this resolve, so
        // ListFollowersAsync is never even asked about them.
        Assert.IsEmpty(notificationRepository.Added);
        Assert.IsEmpty(follows.FollowersQueriedFor);
    }

    [TestMethod]
    public async Task ResolveAsyncCreatesCanonicalWorkAndProvenance()
    {
        var repository = new InMemoryCatalogRepository();
        var provider = new StubProvider(CreateCandidate());
        var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual("Project Hail Mary", result.Work.CanonicalTitle);
        Assert.HasCount(1, result.Work.Authors);
        Assert.AreEqual("Andy Weir", result.Work.Authors.Single().Author.CanonicalName);
        Assert.HasCount(1, result.Work.Editions);
        Assert.AreEqual(EditionFormat.Ebook, result.Work.Editions.Single().Format);
        Assert.AreEqual("9780593135204", result.Work.Editions.Single().Isbn13);
        Assert.HasCount(1, result.Work.SeriesEntries);
        Assert.AreEqual(2.5m, result.Work.SeriesEntries.Single().PositionSort);
        Assert.HasCount(1, repository.ExternalReferences);
        Assert.AreEqual(1, repository.SaveCount);
    }

    [TestMethod]
    public async Task ResolveAsyncPrefersAnExplicitPositionSortOverParsingTheLabel()
    {
        var repository = new InMemoryCatalogRepository();
        var candidate = CreateCandidate() with
        {
            Series = [new BookSeriesCandidate("Project Hail Mary Universe", "3rd", true, PositionSort: 3m)]
        };
        var provider = new StubProvider(candidate);
        var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        var entry = result.Work.SeriesEntries.Single();
        Assert.AreEqual(3m, entry.PositionSort);
        Assert.AreEqual("3rd", entry.PositionLabel);
    }

    [TestMethod]
    public async Task ResolveAsyncMarksANewlyDiscoveredSeriesCompletedWhenTheCandidateSaysSo()
    {
        var repository = new InMemoryCatalogRepository();
        var candidate = CreateCandidate() with
        {
            Series = [new BookSeriesCandidate("Project Hail Mary Universe", "1", true, IsCompleted: true)]
        };
        var provider = new StubProvider(candidate);
        var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.AreEqual(SeriesStatus.Completed, result.Work.SeriesEntries.Single().Series.Status);
    }

    [TestMethod]
    public async Task ResolveAsyncMarksAnExistingUnknownSeriesCompletedWhenALaterCandidateSaysSo()
    {
        var repository = new InMemoryCatalogRepository();
        var existingSeries = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now);
        repository.AddSeries(existingSeries);
        var candidate = CreateCandidate() with
        {
            Series = [new BookSeriesCandidate("Project Hail Mary Universe", "1", true, IsCompleted: true)]
        };
        var provider = new StubProvider(candidate);
        var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.AreSame(existingSeries, result.Work.SeriesEntries.Single().Series);
        Assert.AreEqual(SeriesStatus.Completed, existingSeries.Status);
    }

    [TestMethod]
    public async Task ResolveAsyncReusesExistingProviderReferenceWithoutCallingProvider()
    {
        var repository = new InMemoryCatalogRepository();
        var existing = new Work(
            "Project Hail Mary",
            null,
            null,
            null,
            PublicationStatus.Unknown,
            Now);
        repository.AddWork(existing);
        repository.AddExternalReference(new ExternalReference(
            "stub",
            ExternalReferenceEntityType.Work,
            existing.Id,
            "work-1",
            Now));
        var provider = new StubProvider(CreateCandidate()) { ThrowIfCalled = true };
        var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsFalse(result.WasCreated);
        Assert.AreSame(existing, result.Work);
        Assert.AreEqual(0, provider.GetDetailsCallCount);
        Assert.AreEqual(0, repository.SaveCount);
    }

    [TestMethod]
    public async Task ResolveAsyncReusesWorkWithMatchingIsbnAndAddsProviderReference()
    {
        var repository = new InMemoryCatalogRepository();
        var existing = new Work(
            "Project Hail Mary",
            null,
            null,
            null,
            PublicationStatus.Unknown,
            Now);
        existing.AddEdition(new Edition(
            existing.Id,
            "Project Hail Mary",
            EditionFormat.Ebook,
            "9780593135204",
            null,
            Now));
        repository.AddWork(existing);
        var provider = new StubProvider(CreateCandidate());
        var resolver = CreateResolver(provider, repository, new EmptyFollowRepository(), CreateNotificationService());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsFalse(result.WasCreated);
        Assert.AreSame(existing, result.Work);
        Assert.HasCount(1, repository.ExternalReferences);
        Assert.AreEqual(existing.Id, repository.ExternalReferences.Single().EntityId);
    }

    /// <summary>
    /// The live complaint: searching "threshing day" returned three rows that
    /// were all the same book, with nothing to tell a requester which to pick
    /// -- and whichever they picked became the work title that then matched
    /// no source at all.
    /// </summary>
    [TestMethod]
    public void GroupMatchingCandidatesMergesEditionPackagingOfOneWork()
    {
        var plain = Yarros("Threshing Day", "ol-1");
        var collection = Yarros("Threshing Day (Wing and Claw Collection)", "ol-2");
        var marketing = Yarros(
            "Threshing Day: Return to the Empyrean world with thirteen stories starring your favourite Fourth Wing characters.",
            "gb-1");

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([plain, collection, marketing], "threshing day");

        Assert.HasCount(1, grouped);
        Assert.HasCount(3, grouped[0].MergedSources);
    }

    [TestMethod]
    public void GroupMatchingCandidatesMergesAnnotatedAndIllustratedPackaging()
    {
        var grouped = BookCandidateGrouper.GroupMatchingCandidates(
            [
                Alcott("Little Women", "a"),
                Alcott("Little Women: ( Illustrated Edition)", "b"),
                Alcott("Little Women: An Annotated Edition", "c"),
                Alcott("Little Women: Large Print", "d")
            ],
            "little women");

        Assert.HasCount(1, grouped);
    }

    /// <summary>
    /// The other half of the rule: an omnibus, a combined edition and an
    /// adaptation are genuinely different products and must stay separate
    /// even though they all begin with the requested title.
    /// </summary>
    [TestMethod]
    public void GroupMatchingCandidatesKeepsDifferentProductsApart()
    {
        var grouped = BookCandidateGrouper.GroupMatchingCandidates(
            [
                Alcott("Little Women", "a"),
                Alcott("Little Women: the Trilogy Little Women Little Men Jo's Boys", "b"),
                Alcott("LITTLE WOMEN - Complete Edition: Little Women, Good Wives, Little Men & Jo's Boys", "c"),
                Alcott("Little Women: Adapted for the Stage", "d")
            ],
            "little women");

        Assert.HasCount(4, grouped);
    }

    [TestMethod]
    public void GroupMatchingCandidatesKeepsADifferentAuthorsSameTitleBookApart()
    {
        var alcott = Alcott("Little Women", "a");
        var lawlor = Alcott("Little Women", "b") with { Authors = ["Laurie Lawlor"] };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates([alcott, lawlor], "little women");

        Assert.HasCount(2, grouped);
    }

    private static BookCandidate Yarros(string title, string externalId) =>
        CreateCandidate(title, externalId) with { Authors = ["Rebecca Yarros"], Editions = [], Series = [] };

    private static BookCandidate Alcott(string title, string externalId) =>
        CreateCandidate(title, externalId) with { Authors = ["Louisa May Alcott"], Editions = [], Series = [] };

    /// <summary>
    /// The live complaint: searching "moby dick" ranked five adaptations/comics
    /// -- each crediting an adapter or illustrator alongside Melville -- ahead
    /// of the plain, single-author edition, because every one of them tied on
    /// title match and fell through to alphabetical-by-first-author
    /// ("Academic Industries" / "Archie Oliver" / "Bill Sienkiewicz" all sort
    /// before "Herman Melville"). The plain edition must rank first.
    /// </summary>
    [TestMethod]
    public void GroupMatchingCandidatesRanksThePlainEditionAboveCreditedAdaptations()
    {
        var plain = Melville("Moby Dick", ["Herman Melville"], "globe-fearon");
        var comic1 = Melville("Moby Dick", ["Archie Oliver", "Herman Melville"], "playmore");
        var comic2 = Melville("Moby Dick", ["Bill Sienkiewicz", "Herman Melville"], "berkley");
        var youngReaders = Melville("Moby Dick", ["Janet Lorimer", "Herman Melville"], "saalfield");

        var grouped = BookCandidateGrouper.GroupMatchingCandidates(
            [comic2, youngReaders, comic1, plain], "moby dick");

        Assert.AreEqual("globe-fearon", grouped[0].ExternalId);
    }

    private static BookCandidate Melville(string title, IReadOnlyList<string> authors, string externalId) =>
        CreateCandidate(title, externalId) with { Authors = authors, Editions = [], Series = [] };

    /// <summary>
    /// The exact "moby dick" dataset reported live: nine candidates tie on
    /// title match (all titled plainly "Moby Dick"), and the previous
    /// author-count tiebreak had nothing to act on because every one of them
    /// happened to credit two people, including both real adaptations
    /// ("Bill Sienkiewicz, Herman Melville" -- a comic) and a legitimate
    /// scholarly edition ("Herman Melville, Nigel Cliff" -- editor's
    /// introduction). Four of the nine describe themselves as an adaptation
    /// in plain text; the scholarly edition does not and must rank first.
    /// </summary>
    [TestMethod]
    public void GroupMatchingCandidatesRanksADescribedNonAdaptationAboveTextuallyConfirmedAdaptations()
    {
        var scholarly = Melville("Moby Dick", ["Herman Melville", "Nigel Cliff"], "flame-tree") with
        {
            Description = "Moby Dick is the story of Captain Ahab's quest to avenge the whale that 'reaped' his leg."
        };
        var comic1 = Melville("Moby Dick", ["Bill Sienkiewicz", "Herman Melville"], "berkley") with
        {
            Description = "Presented in comic book format."
        };
        var comic2 = Melville("Moby Dick", ["Lance Stahlberg", "Herman Melville"], "campfire") with
        {
            Description = "Retells in graphic novel format Melville's story."
        };
        var forYoungReaders = Melville("Moby Dick", ["Shirley Bogart", "Herman Melville"], "baronet") with
        {
            Description = "A specially adapted version...abridged, simplified and condensed for young readers."
        };
        var retelling = Melville("Moby Dick", ["T. W. Robinson", "Herman Melville"], "bendon") with
        {
            Description = "Retells the story of the crazed Captain Ahab in his hunt for the great white whale."
        };
        // Undescribed stubs: real Open Library records with no description at
        // all -- indistinguishable from the scholarly edition by text alone,
        // so they must not outrank it either.
        var stub1 = Melville("Moby Dick", ["Academic Industries", "Herman Melville"], "academic") with
        {
            Description = null
        };
        var stub2 = Melville("Moby Dick", ["Albert L. Kanter", "Herman Melville"], "acclaim") with
        {
            Description = null
        };

        var grouped = BookCandidateGrouper.GroupMatchingCandidates(
            [comic1, stub2, retelling, scholarly, forYoungReaders, stub1, comic2], "moby dick");

        Assert.AreEqual("flame-tree", grouped[0].ExternalId);
    }

    // ---- typed faults, ambiguity and notices (logic only: the repository here is an in-memory double) ----------------------------

    [TestMethod]
    public async Task AnIsbnSetSpanningTwoWorksIsATypedCollisionThatWritesNothing()
    {
        var repository = new InMemoryCatalogRepository();
        foreach (var isbn in new[] { "9780593135204", "9780593396166" })
        {
            var work = new Work($"Held {isbn}", null, null, null, PublicationStatus.Unknown, Now);
            work.AddEdition(new Edition(work.Id, "Held", EditionFormat.Ebook, isbn, null, Now));
            repository.AddWork(work);
        }

        var candidate = CreateCandidate() with
        {
            Editions = [new BookEditionCandidate("Both", "9780593135204", "Ebook", null), new BookEditionCandidate("Both", "9780593396166", "Ebook", null)]
        };
        var resolver = CreateResolver(new StubProvider(candidate), repository, new EmptyFollowRepository(), CreateNotificationService());

        var thrown = await Assert.ThrowsExactlyAsync<CatalogResolutionException>(
            async () => await resolver.ResolveAsync("stub", "work-1", CancellationToken.None));

        Assert.AreEqual(CatalogResolutionFault.IsbnCollision, thrown.Fault);
        Assert.IsEmpty(repository.ExternalReferences, "No guessed winner: no reference was added to either Work.");
        Assert.AreEqual(0, repository.SaveCount);
    }

    [TestMethod]
    public async Task ASourceThatIsInstalledButDisabledIsNeverAskedAndAnUnknownOneIsReportedSeparately()
    {
        var disabled = new StubProvider(CreateCandidate()) { ThrowIfCalled = true };
        var repository = new InMemoryCatalogRepository();
        var resolver = new CatalogWorkResolver(
            new DisabledButRegistered("stub"), repository, new PassThroughWriteScope(repository, new EmptyFollowRepository()),
            CreateNotificationService(), new FixedClock());

        var notEnabled = await Assert.ThrowsExactlyAsync<CatalogResolutionException>(
            async () => await resolver.ResolveAsync("stub", "work-1", CancellationToken.None));
        var unknown = await Assert.ThrowsExactlyAsync<CatalogResolutionException>(
            async () => await resolver.ResolveAsync("not-installed", "work-1", CancellationToken.None));

        Assert.AreEqual(CatalogResolutionFault.ProviderNotEnabled, notEnabled.Fault);
        Assert.AreEqual(CatalogResolutionFault.UnknownProvider, unknown.Fault);
        Assert.AreEqual(0, disabled.GetDetailsCallCount);
    }

    [TestMethod]
    public async Task ASavedReferenceResolvesEvenWhenItsSourceIsDisabled()
    {
        var repository = new InMemoryCatalogRepository();
        var existing = new Work("Saved", null, null, null, PublicationStatus.Unknown, Now);
        repository.AddWork(existing);
        repository.AddExternalReference(new ExternalReference("stub", ExternalReferenceEntityType.Work, existing.Id, "work-1", Now));
        var resolver = new CatalogWorkResolver(
            new DisabledButRegistered("stub"), repository, new PassThroughWriteScope(repository, new EmptyFollowRepository()),
            CreateNotificationService(), new FixedClock());

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.AreSame(existing, result.Work);
        Assert.AreEqual(CatalogResolutionOutcome.Existing, result.Outcome);
        Assert.AreEqual(0, repository.SaveCount, "A saved reference is a read: no write at all.");
    }

    [TestMethod]
    public async Task AMissingRecordIsATypedNotFound()
    {
        var repository = new InMemoryCatalogRepository();
        var resolver = CreateResolver(new StubProvider(CreateCandidate()), repository, new EmptyFollowRepository(), CreateNotificationService());

        var thrown = await Assert.ThrowsExactlyAsync<CatalogResolutionException>(
            async () => await resolver.ResolveAsync("stub", "absent", CancellationToken.None));

        Assert.AreEqual(CatalogResolutionFault.CandidateNotFound, thrown.Fault);
    }

    [TestMethod]
    public async Task EveryWriteScopeFailureStatusBecomesItsOwnTypedFault()
    {
        var expected = new Dictionary<CatalogWriteStatus, CatalogResolutionFault>
        {
            [CatalogWriteStatus.Busy] = CatalogResolutionFault.Busy,
            [CatalogWriteStatus.Conflict] = CatalogResolutionFault.Conflict,
            [CatalogWriteStatus.OutcomeUnknown] = CatalogResolutionFault.OutcomeUnknown,
            [CatalogWriteStatus.Unavailable] = CatalogResolutionFault.Unavailable
        };
        foreach (var (status, fault) in expected)
        {
            var repository = new InMemoryCatalogRepository();
            var resolver = new CatalogWorkResolver(
                new ActiveProviders(new StubProvider(CreateCandidate())), repository, new FixedStatusScope(status),
                CreateNotificationService(), new FixedClock());

            var thrown = await Assert.ThrowsExactlyAsync<CatalogResolutionException>(
                async () => await resolver.ResolveAsync("stub", "work-1", CancellationToken.None));

            Assert.AreEqual(fault, thrown.Fault, status.ToString());
        }
    }

    [TestMethod]
    public async Task ANameSharedByTwoAuthorsGivesTheNewWorkItsOwnAuthorAndNotifiesNoNamesakeFollower()
    {
        var repository = new InMemoryCatalogRepository();
        var first = new Author("Andy Weir", null, Now);
        var second = new Author("Andy Weir", null, Now.AddDays(1));
        repository.AddAuthor(first);
        repository.AddAuthor(second);
        var follows = new InMemoryFollowRepository();
        follows.Seed(new Follow(Guid.NewGuid(), FollowSubjectType.Author, first.Id, Now));
        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());
        var diagnostics = new RecordingDiagnostics();
        var resolver = CreateResolver(new StubProvider(CreateCandidate()), repository, follows, notifications, diagnostics);

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        var credit = result.Work.Authors.Single().Author;
        Assert.AreNotSame(first, credit);
        Assert.AreNotSame(second, credit);
        Assert.AreEqual("Andy Weir", credit.CanonicalName);
        Assert.IsEmpty(first.WorkAuthors, "The first namesake gained no Work.");
        Assert.IsEmpty(second.WorkAuthors, "The second namesake gained no Work.");
        Assert.IsEmpty(follows.FollowersQueriedFor.Where(query => query.SubjectType == FollowSubjectType.Author), "No namesake's followers were even looked up.");
        Assert.IsEmpty(notificationRepository.Added.Where(item => item.Category == NotificationCategories.AuthorNewWorkDetected));
        var ambiguous = result.Diagnostics.AmbiguousAuthors.Single();
        Assert.AreEqual(credit.Id, ambiguous.NewAuthorId);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, ambiguous.NamesakeAuthorIds.ToArray());
        Assert.HasCount(1, diagnostics.Reports, "A committed creation reports its unbound credits once.");
    }

    [TestMethod]
    public async Task ANameSharedByTwoSeriesCreatesNoMembershipAndNoCompletionFlag()
    {
        var repository = new InMemoryCatalogRepository();
        var first = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now);
        var second = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now.AddDays(1));
        repository.AddSeries(first);
        repository.AddSeries(second);
        var follows = new InMemoryFollowRepository();
        follows.Seed(new Follow(Guid.NewGuid(), FollowSubjectType.Series, first.Id, Now));
        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());
        var candidate = CreateCandidate() with { Series = [new BookSeriesCandidate("Project Hail Mary Universe", "2", true, IsCompleted: true)] };
        var resolver = CreateResolver(new StubProvider(candidate), repository, follows, notifications);

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsEmpty(result.Work.SeriesEntries);
        Assert.IsEmpty(first.Entries);
        Assert.IsEmpty(second.Entries);
        Assert.AreEqual(SeriesStatus.Unknown, first.Status, "The completion flag was not applied to a namesake.");
        Assert.AreEqual(SeriesStatus.Unknown, second.Status);
        Assert.IsEmpty(notificationRepository.Added, "No follower of an ambiguous series is told anything.");
        Assert.AreEqual("Project Hail Mary Universe", result.Diagnostics.OmittedSeriesMemberships.Single().ReportedName);
    }

    [TestMethod]
    public async Task ARecoveredResultIsNeverNotifiedOrReportedAgain()
    {
        var repository = new InMemoryCatalogRepository();
        var author = new Author("Andy Weir", null, Now);
        repository.AddAuthor(author);
        var follows = new InMemoryFollowRepository();
        follows.Seed(new Follow(Guid.NewGuid(), FollowSubjectType.Author, author.Id, Now));
        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());
        var diagnostics = new RecordingDiagnostics();
        var resolver = new CatalogWorkResolver(
            new ActiveProviders(new StubProvider(CreateCandidate())), repository, new RecoveringScope(repository, follows),
            notifications, new FixedClock(), diagnostics);

        var result = await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.IsNotNull(result.Work);
        Assert.IsEmpty(notificationRepository.Added, "No notice is emitted from a recovered result, so none can be duplicated.");
        Assert.IsEmpty(diagnostics.Reports);
    }

    [TestMethod]
    public async Task CreationNoticesNameACatalogAdditionNotARelease()
    {
        var repository = new InMemoryCatalogRepository();
        var author = new Author("Andy Weir", null, Now);
        var series = new Series("Project Hail Mary Universe", SeriesStatus.Unknown, Now);
        repository.AddAuthor(author);
        repository.AddSeries(series);
        var follows = new InMemoryFollowRepository();
        follows.Seed(new Follow(Guid.NewGuid(), FollowSubjectType.Author, author.Id, Now));
        follows.Seed(new Follow(Guid.NewGuid(), FollowSubjectType.Series, series.Id, Now));
        var notificationRepository = new RecordingNotificationRepository();
        var notifications = new NotificationService(notificationRepository, new StubCurrentUser(), new FixedClock());
        var resolver = CreateResolver(new StubProvider(CreateCandidate()), repository, follows, notifications);

        await resolver.ResolveAsync("stub", "work-1", CancellationToken.None);

        Assert.HasCount(2, notificationRepository.Added);
        foreach (var notice in notificationRepository.Added)
        {
            StringAssert.StartsWith(notice.Title, "Added to the catalog:");
            StringAssert.Contains(notice.Title, "Project Hail Mary");
            StringAssert.Contains(notice.Detail, "added to Family Librarian's catalog and may be an older title");
            Assert.IsFalse(notice.Title.Contains("New from", StringComparison.Ordinal) || notice.Title.Contains("joined", StringComparison.Ordinal),
                "No release wording.");
        }
    }

    private sealed class RecordingDiagnostics : ICatalogResolutionDiagnostics
    {
        public List<(Guid WorkId, CatalogResolutionDiagnostics Diagnostics)> Reports { get; } = [];

        public void Report(Guid workId, CatalogResolutionDiagnostics diagnostics) => Reports.Add((workId, diagnostics));
    }

    /// <summary>Installed but switched off: a lookup for the active provider finds nothing, yet the id is registered.</summary>
    private sealed class DisabledButRegistered(string registeredId) : IActiveMetadataProviderResolver
    {
        public Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IBookMetadataProvider>>([]);

        public Task<IBookMetadataProvider?> FindActiveProviderAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<IBookMetadataProvider?>(null);

        public bool IsRegistered(string providerId) => providerId == registeredId;
    }

    private sealed class FixedStatusScope(CatalogWriteStatus status) : ICatalogWriteScope
    {
        public Task<CatalogWriteResult<T>> ExecuteAsync<T>(
            Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
            Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verifyAfterUnknownCommit,
            CancellationToken cancellationToken) where T : class =>
            Task.FromResult(new CatalogWriteResult<T>(status, null));
    }

    /// <summary>Runs the mutation, then reports its result as recovered after an unknown commit.</summary>
    private sealed class RecoveringScope(InMemoryCatalogRepository repository, IFollowRepository follows) : ICatalogWriteScope
    {
        public async Task<CatalogWriteResult<T>> ExecuteAsync<T>(
            Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
            Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verifyAfterUnknownCommit,
            CancellationToken cancellationToken) where T : class
        {
            var attempted = await mutation(new PassThroughSession(repository, follows), cancellationToken);
            return new CatalogWriteResult<T>(CatalogWriteStatus.Recovered, attempted);
        }
    }

    private sealed class PassThroughSession(InMemoryCatalogRepository repository, IFollowRepository follows) : ICatalogWriteSession
    {
        public ICatalogMutationRepository Catalog => repository;

        public IFollowRepository Follows => follows;

        public CatalogWriteRetryCause RetryCause => CatalogWriteRetryCause.None;
    }

    private static BookCandidate CreateCandidate(

        string title = "Project Hail Mary",
        string externalId = "work-1") => new(
        "stub",
        "Stub catalog",
        externalId,
        title,
        ["Andy Weir"],
        "A science-fiction novel.",
        null,
        new DateOnly(2021, 5, 4),
        [new BookEditionCandidate(title, "9780593135204", "Ebook", new DateOnly(2021, 5, 4))],
        [new BookSeriesCandidate("Project Hail Mary Universe", "2.5", true)]);

    // No test here ever follows a series/author, so ListFollowersAsync always
    // returns empty and NotificationService is never actually called through
    // it -- these exist only to satisfy CatalogWorkResolver's constructor.
    private static NotificationService CreateNotificationService() =>
        new(new ThrowingNotificationRepository(), new StubCurrentUser(), new FixedClock());

    /// <summary>
    /// A resolver over the in-memory repository double. The double stands in for the database here, so these tests prove the
    /// resolver's decisions only; the real-repository, lock and process behavior is proven by the PostgreSQL tests.
    /// </summary>
    private static CatalogWorkResolver CreateResolver(
        IBookMetadataProvider provider,
        InMemoryCatalogRepository repository,
        IFollowRepository follows,
        NotificationService notifications,
        ICatalogResolutionDiagnostics? diagnostics = null) =>
        new(new ActiveProviders(provider), repository, new PassThroughWriteScope(repository, follows), notifications, new FixedClock(), diagnostics);

    /// <summary>Every provider it is given is enabled; one it is not given is not installed.</summary>
    private sealed class ActiveProviders(params IBookMetadataProvider[] providers) : IActiveMetadataProviderResolver
    {
        public Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IBookMetadataProvider>>(providers);

        public Task<IBookMetadataProvider?> FindActiveProviderAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(providers.FirstOrDefault(provider => provider.Id == providerId));

        public bool IsRegistered(string providerId) => providers.Any(provider => provider.Id == providerId);
    }

    /// <summary>Runs the mutation once against the supplied repository, with no transaction, lock or retry: a logic-only double.</summary>
    private sealed class PassThroughWriteScope(InMemoryCatalogRepository repository, IFollowRepository follows) : ICatalogWriteScope
    {
        private sealed class Session(InMemoryCatalogRepository repository, IFollowRepository follows) : ICatalogWriteSession
        {
            public ICatalogMutationRepository Catalog => repository;

            public IFollowRepository Follows => follows;

            public CatalogWriteRetryCause RetryCause => CatalogWriteRetryCause.None;
        }

        public async Task<CatalogWriteResult<T>> ExecuteAsync<T>(
            Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
            Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verifyAfterUnknownCommit,
            CancellationToken cancellationToken) where T : class =>
            new(CatalogWriteStatus.Committed, await mutation(new Session(repository, follows), cancellationToken));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? DisplayName => null;
    }

    private sealed class EmptyFollowRepository : IFollowRepository
    {
        public Task<Follow?> FindAsync(
            Guid userId, FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<Follow?>(null);

        public Task<IReadOnlyList<Follow>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Follow>>([]);

        public Task<IReadOnlyList<Follow>> ListFollowersAsync(
            FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Follow>>([]);

        public void Add(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public void Remove(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryFollowRepository : IFollowRepository
    {
        private readonly List<Follow> _rows = [];

        public List<(FollowSubjectType SubjectType, Guid SubjectId)> FollowersQueriedFor { get; } = [];

        public void Seed(Follow follow) => _rows.Add(follow);

        public Task<Follow?> FindAsync(
            Guid userId, FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<Follow>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<Follow>> ListFollowersAsync(
            FollowSubjectType subjectType, Guid subjectId, CancellationToken cancellationToken)
        {
            FollowersQueriedFor.Add((subjectType, subjectId));
            return Task.FromResult<IReadOnlyList<Follow>>(_rows
                .Where(follow => follow.SubjectType == subjectType && follow.SubjectId == subjectId)
                .ToArray());
        }

        public void Add(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public void Remove(Follow follow) => throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingNotificationRepository : INotificationRepository
    {
        public List<NotificationEvent> Added { get; } = [];

        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience, Guid? recipientUserId, string category,
            string? subjectType, string? subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<NotificationEvent?>(null);

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken)
        {
            Added.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task RemoveReceiptsAsync(
            IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingNotificationRepository : INotificationRepository
    {
        public Task<NotificationEvent?> FindLatestAsync(
            NotificationAudience audience, Guid? recipientUserId, string category,
            string? subjectType, string? subjectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AddAsync(NotificationEvent notification, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NotificationReceipt>> ListReceiptsAsync(
            Guid notificationEventId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task RemoveReceiptsAsync(
            IReadOnlyList<NotificationReceipt> receipts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<(NotificationEvent Event, NotificationReceipt? Receipt)>> ListForViewerAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NotificationReceipt?> FindReceiptAsync(
            Guid notificationEventId, Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AddReceiptAsync(NotificationReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class StubProvider(BookCandidate candidate) : IBookMetadataProvider
    {
        public bool ThrowIfCalled { get; init; }

        public int GetDetailsCallCount { get; private set; }

        public string Id => "stub";

        public string DisplayName => "Stub catalog";

        public Task<BookCandidateSearchPage> SearchAsync(
            BookSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([candidate], false));

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            GetDetailsCallCount++;
            if (ThrowIfCalled)
            {
                throw new InvalidOperationException("The provider should not be called.");
            }

            return Task.FromResult<BookCandidate?>(
                string.Equals(externalId, candidate.ExternalId, StringComparison.Ordinal) ? candidate : null);
        }
    }

    private sealed class InMemoryCatalogRepository : ICatalogRepository, ICatalogMutationRepository
    {
        private readonly List<Work> _works = [];
        private readonly List<Author> _authors = [];
        private readonly List<Series> _series = [];

        public List<ExternalReference> ExternalReferences { get; } = [];

        public int SaveCount { get; private set; }

        public Task<Work?> FindWorkByExternalReferenceAsync(
            string providerId,
            string externalId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_works.SingleOrDefault(work => ExternalReferences.Any(reference =>
                reference.EntityType == ExternalReferenceEntityType.Work &&
                reference.EntityId == work.Id &&
                reference.ProviderId == providerId &&
                reference.ExternalId == externalId)));

        public Task<Work?> FindWorkByIsbn13Async(
            IReadOnlyCollection<string> isbn13s,
            CancellationToken cancellationToken) =>
            Task.FromResult(_works.SingleOrDefault(work => work.Editions.Any(edition =>
                edition.Isbn13 is not null && isbn13s.Contains(edition.Isbn13))));

        public Task<IReadOnlyList<Work>> ListWorksByIsbn13Async(
            IReadOnlyCollection<string> isbn13s,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Work>>(_works.Where(work => work.Editions.Any(edition =>
                edition.Isbn13 is not null && isbn13s.Contains(edition.Isbn13))).ToArray());

        public Task<IReadOnlyList<Author>> ListAuthorsByNormalizedNameAsync(
            string normalizedName,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Author>>(_authors.Where(author => author.NormalizedName == normalizedName).ToArray());

        public Task<IReadOnlyList<Series>> ListSeriesByNormalizedNameAsync(
            string normalizedName,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Series>>(_series.Where(series => series.NormalizedName == normalizedName).ToArray());

        public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult(_works.SingleOrDefault(work => work.Id == workId));

        public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
            Guid workId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalReference>>(ExternalReferences
                .Where(reference => reference.EntityType == ExternalReferenceEntityType.Work &&
                    reference.EntityId == workId)
                .ToArray());

        public Task<Author?> FindAuthorByNormalizedNameAsync(
            string normalizedName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_authors.SingleOrDefault(author => author.NormalizedName == normalizedName));

        public Task<Series?> FindSeriesByNormalizedNameAsync(
            string normalizedName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_series.SingleOrDefault(series => series.NormalizedName == normalizedName));

        public Task<Series?> GetSeriesAsync(Guid seriesId, CancellationToken cancellationToken) =>
            Task.FromResult(_series.SingleOrDefault(series => series.Id == seriesId));

        public Task<Author?> GetAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
            Task.FromResult(_authors.SingleOrDefault(author => author.Id == authorId));

        public void AddWork(Work work) => _works.Add(work);

        public void AddAuthor(Author author) => _authors.Add(author);

        public void AddSeries(Series series) => _series.Add(series);

        public void AddExternalReference(ExternalReference externalReference) =>
            ExternalReferences.Add(externalReference);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
