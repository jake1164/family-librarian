using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FamilyLibrarian.Web.Tests.Harness.RigBooks;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The activated ordinary Resolve on real PostgreSQL: what a created Work records, when a record attaches to a saved Work and when it
/// becomes its own, which editions may be written or count as proof, how ambiguous names bind, and what a known reference never does.
/// Every case boots the production host over its own migrated database; only provider transports and the clock are doubles.
/// </summary>
[TestClass]
public sealed class CatalogObservationResolveTests
{
    private const string Isbn1 = "9781000000001";
    private const string Isbn2 = "9781000000018";
    private const string Isbn3 = "9781000000025";
    private const string IsbnDe = "9781000000032";

    // ------------------------------------------------------------------------------------------------------------ creation

    [TestMethod]
    public async Task ACreatedWorkRecordsItsEvidenceAndEverySelectedValueIsBackedByItsClaim()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["b1"] = Book("demo", "b1", "Evidence Book", ["Ada Writer"],
            [Edition("Evidence Book", Isbn1, "Ebook", "en", "Pub One", new DateOnly(2021, 5, 4))],
            [Series("Evidence Cycle", "2")], "Hello world.", new DateOnly(1999, 1, 1), DatePrecision.Year, DateBasis.Original, "en",
            "https://example.test/cover.jpg", 321);

        var result = await rig.ResolveAsync("demo", "b1");
        var work = await rig.LoadWorkAsync(result.Work.Id);

        Assert.IsTrue(result.WasCreated);
        Assert.IsNull(result.Detail);
        Assert.AreEqual("Text", work.VersionClass);
        Assert.AreEqual(new DateOnly(1999, 1, 1), work.FirstPublicationDate);
        Assert.AreEqual(DatePrecision.Year, work.FirstPublicationPrecision);
        Assert.AreEqual(DateBasis.Original, work.FirstPublicationBasis);
        Assert.AreEqual(PublicationStatus.Published, work.PublicationStatus);
        Assert.AreEqual("Hello world.", work.Description);
        Assert.AreEqual("https://example.test/cover.jpg", work.CoverUrl);
        Assert.AreEqual(321, work.CatalogPageCount);

        var credit = work.Authors.Single();
        Assert.AreEqual(AuthorBindingBasis.Created, credit.BindingBasis);
        var edition = work.Editions.Single();
        Assert.AreEqual("en", edition.Language);
        Assert.AreEqual("Pub One", edition.Publisher);
        Assert.AreEqual(EditionIdentityBasis.Created, edition.IdentityBasis);
        Assert.AreEqual("demo", edition.SourceProviderId);
        Assert.AreEqual(DatePrecision.Day, edition.PublicationPrecision);
        var entry = work.SeriesEntries.Single();
        Assert.AreEqual(SeriesBindingBasis.Created, entry.BindingBasis);
        Assert.AreEqual(SeriesPositionState.SingleSource, entry.PositionState);
        Assert.AreEqual(2m, entry.PositionSort);
        Assert.AreEqual("2", entry.PositionLabel);

        var claims = await rig.ClaimsAsync(work.Id);
        CollectionAssert.AreEquivalent(
            new[]
            {
                MetadataClaimField.OriginalPublicationDate, MetadataClaimField.EditionPublicationDate, MetadataClaimField.Description,
                MetadataClaimField.CoverUrl, MetadataClaimField.PageCount, MetadataClaimField.SeriesPosition
            },
            claims.Select(claim => claim.Field).ToArray());
        Assert.IsTrue(claims.All(claim => claim is { IsCurrent: true, IsSelected: true, ProviderId: "demo", SourceEntityId: "b1" }),
            "Each shown value is its own selected claim, attributed to the record that said it.");
        Assert.AreEqual(entry.SeriesId.ToString("D"), claims.Single(claim => claim.Field == MetadataClaimField.SeriesPosition).ScopeKey);
        Assert.AreEqual(Isbn1, claims.Single(claim => claim.Field == MetadataClaimField.EditionPublicationDate).ScopeKey);

        var observation = (await rig.ObservationsAsync(work.Id)).Single();
        using var snapshot = System.Text.Json.JsonDocument.Parse(observation.Snapshot);
        Assert.AreEqual("Observed", snapshot.RootElement.GetProperty("outcome").GetString());
        Assert.IsFalse(observation.Snapshot.Contains("Hello world.", StringComparison.Ordinal), "The snapshot never carries free text it can reference by hash.");
        Assert.IsFalse(rig.Demo.DetailCalls > 1, "One provider call, outside the transaction.");
    }

    [TestMethod]
    public async Task PublicationStatusFollowsTheClockAndThePrecisionTheSourceStated()
    {
        await using var rig = await ObservationRig.CreateAsync();
        // The pinned clock is 2026-06-01: a future day, a year that has not ended, a past year and a past month.
        var cases = new (string Id, DateOnly Date, DatePrecision Precision, PublicationStatus Expected)[]
        {
            ("future-day", new DateOnly(2027, 1, 1), DatePrecision.Day, PublicationStatus.Forthcoming),
            ("this-year", new DateOnly(2026, 1, 1), DatePrecision.Year, PublicationStatus.Unknown),
            ("past-year", new DateOnly(2025, 1, 1), DatePrecision.Year, PublicationStatus.Published),
            ("past-month", new DateOnly(2026, 4, 1), DatePrecision.Month, PublicationStatus.Published)
        };
        foreach (var item in cases)
        {
            rig.Demo.Records[item.Id] = Book("demo", item.Id, $"Dated {item.Id}", ["Dated Author"], date: item.Date, precision: item.Precision, basis: DateBasis.Original);
        }

        foreach (var item in cases)
        {
            var resolved = await rig.ResolveAsync("demo", item.Id);
            var work = await rig.LoadWorkAsync(resolved.Work.Id);
            Assert.AreEqual(item.Expected, work.PublicationStatus, item.Id);
            Assert.AreEqual(item.Precision, work.FirstPublicationPrecision, item.Id);
        }
    }

    [TestMethod]
    public async Task ARecordWithNoDateOrDescriptionWritesNoClaimForTheMissingFacts()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["bare"] = Book("demo", "bare", "Bare Book", ["Bare Author"], description: null);

        var work = (await rig.ResolveAsync("demo", "bare")).Work;

        Assert.IsEmpty(await rig.ClaimsAsync(work.Id), "Absence is not a statement.");
        Assert.HasCount(1, await rig.ObservationsAsync(work.Id));
        Assert.IsNull((await rig.LoadWorkAsync(work.Id)).FirstPublicationDate);
    }

    // ------------------------------------------------------------------------------------------------------------ attach (P-A1)

    [TestMethod]
    public async Task ASecondSourceSharingAnIsbnAttachesAndAddsOnlyEvidenceNeverAuthorsOrNotices()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        var follower = await FollowAuthorAsync(rig, "Ada Writer");
        rig.Demo.Records["d1"] = Book("demo", "d1", "Shared Book", ["Ada Writer"],
            [Edition("Shared Book", Isbn1, "Hardcover", "en")], [Series("Shared Cycle", "1")], "Demo description.", new DateOnly(2000, 3, 4));
        var created = await rig.ResolveAsync("demo", "d1");
        var notices = await rig.DbAsync(database => database.NotificationEvents.CountAsync());

        rig.OpenLibrary.Records["ol1"] = Book("openlibrary", "ol1", "Shared Book", ["Ada Writer", "Co Writer"],
            [Edition("Shared Book", Isbn1, "Hardcover", "en"), Edition("Shared Book", Isbn2, "Paperback", "en", "Open Press")],
            [Series("Shared Cycle", "1")], "Open Library description.", new DateOnly(2000, 1, 1), DatePrecision.Year, DateBasis.Original,
            "en", "https://example.test/ol.jpg", 400);
        var attached = await rig.ResolveAsync("openlibrary", "ol1");

        Assert.IsFalse(attached.WasCreated);
        Assert.AreEqual(CatalogResolutionOutcome.Attached, attached.Outcome);
        Assert.AreEqual(created.Work.Id, attached.Work.Id);
        var work = await rig.LoadWorkAsync(created.Work.Id);
        Assert.HasCount(1, work.Authors, "Attach never adds an author to an existing Work.");
        Assert.HasCount(2, work.Editions);
        var added = work.Editions.Single(edition => edition.Isbn13 == Isbn2);
        Assert.AreEqual(EditionIdentityBasis.Isbn, added.IdentityBasis);
        Assert.AreEqual("openlibrary", added.SourceProviderId);
        Assert.AreEqual("Open Press", added.Publisher);
        Assert.AreEqual(2, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference => reference.EntityId == work.Id)));
        Assert.AreEqual(notices, await rig.DbAsync(database => database.NotificationEvents.CountAsync()), "An attach never notifies.");
        Assert.AreEqual(follower, await rig.DbAsync(database => database.Follows.Select(item => item.UserId).SingleAsync()));

        // Sticky scalars: the first accepted description stays; the other source's text is kept as an attributed, unselected claim.
        Assert.AreEqual("Demo description.", work.Description);
        var claims = await rig.ClaimsAsync(work.Id);
        var descriptions = claims.Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, descriptions);
        Assert.AreEqual("demo", descriptions.Single(claim => claim.IsSelected).ProviderId);
        Assert.AreEqual("openlibrary", descriptions.Single(claim => !claim.IsSelected).ProviderId);

        // A gap is filled by the new source: the cover and page count were unknown.
        Assert.AreEqual("https://example.test/ol.jpg", work.CoverUrl);
        Assert.AreEqual(400, work.CatalogPageCount);

        // An original year outranks the first source's day, which was an unknown-basis value, and the original claim is the selected one.
        var dates = claims.Where(claim => claim.Field == MetadataClaimField.OriginalPublicationDate).ToArray();
        Assert.HasCount(2, dates);
        Assert.AreEqual("openlibrary", dates.Single(claim => claim.IsSelected).ProviderId);
        Assert.AreEqual(new DateOnly(2000, 1, 1), work.FirstPublicationDate);
        Assert.AreEqual(DatePrecision.Year, work.FirstPublicationPrecision);
        Assert.AreEqual(DateBasis.Original, work.FirstPublicationBasis);

        // Series: both sources agree, and the state says so without claiming the sources are independent.
        Assert.AreEqual(SeriesPositionState.Agreed, work.SeriesEntries.Single().PositionState);
    }

    // ------------------------------------------------------------------------------------------------------------ collisions

    [TestMethod]
    [DataRow("Author")]
    [DataRow("Title")]
    [DataRow("Year")]
    [DataRow("Language")]
    [DataRow("Class")]
    public async Task ASharedIsbnThatFailsAnIdentityCheckBecomesItsOwnWorkAndTheHeldWorkIsByteUnchanged(string reason)
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["held"] = Book("demo", "held", "The Held Book", ["Held Author"], [Edition("The Held Book", Isbn1, "Paperback", "en")],
            date: new DateOnly(1990, 1, 1), basis: DateBasis.Original, language: "en");
        var held = (await rig.ResolveAsync("demo", "held")).Work;
        var before = await rig.CatalogRowsAsync();

        var other = reason switch
        {
            "Author" => Book("openlibrary", "x", "The Held Book", ["Someone Else"], [Edition("The Held Book", Isbn1, "Paperback", "en")], language: "en"),
            "Title" => Book("openlibrary", "x", "A Different Book Entirely", ["Held Author"], [Edition("A Different Book Entirely", Isbn1, "Paperback", "en")], language: "en"),
            "Year" => Book("openlibrary", "x", "The Held Book", ["Held Author"], [Edition("The Held Book", Isbn1, "Paperback", "en")],
                date: new DateOnly(2015, 1, 1), basis: DateBasis.Original, language: "en"),
            "Language" => Book("openlibrary", "x", "The Held Book", ["Held Author"], [Edition("The Held Book", Isbn1, "Paperback", "de", null)], language: "de"),
            _ => Book("openlibrary", "x", "The Held Book (Abridged)", ["Held Author"], [Edition("The Held Book (Abridged)", Isbn1, "Paperback", "en")], language: "en")
        };
        rig.OpenLibrary.Records["x"] = other;

        var result = await rig.ResolveAsync("openlibrary", "x");

        Assert.IsTrue(result.WasCreated, "A separate Work, so the user is served.");
        Assert.AreNotEqual(held.Id, result.Work.Id);
        Assert.AreEqual(ObservationKind.Collision, result.Detail!.Kind);
        Assert.AreEqual(reason, result.Detail.Reason);
        Assert.IsEmpty(result.Work.Editions, "The contested ISBN stays with the Work that holds it.");
        var after = await rig.CatalogRowsAsync();
        foreach (var row in before) CollectionAssert.Contains(after, row, "No pre-existing row value changed.");
    }

    [TestMethod]
    public async Task AnOverlapOnlyOnAnAbridgedEditionIsNoProofSoItIsASeparateWorkWithItsStandardEdition()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["held"] = Book("demo", "held", "Dune", ["Frank Herbert"], [Edition("Dune (Abridged Edition)", Isbn2, "Ebook")], description: null);
        // The held Work happens to own the ISBN of the abridged edition only.
        var held = await rig.DbAsync(async database =>
        {
            var work = new Work("Dune", null, null, null, PublicationStatus.Unknown, ObservationRig.Start);
            work.AddEdition(new Edition(work.Id, "Dune", EditionFormat.Ebook, Isbn2, null, ObservationRig.Start));
            database.Works.Add(work);
            await Task.CompletedTask;
            return work.Id;
        });
        var before = await rig.CatalogRowsAsync();
        rig.OpenLibrary.Records["x"] = Book("openlibrary", "x", "Dune", ["Frank Herbert"],
            [Edition("Dune", Isbn1, "Paperback"), Edition("Dune (Abridged Edition)", Isbn2, "Ebook")]);

        var result = await rig.ResolveAsync("openlibrary", "x");

        Assert.IsTrue(result.WasCreated);
        Assert.AreNotEqual(held, result.Work.Id);
        Assert.AreEqual(ObservationKind.NoProof, result.Detail!.Kind);
        Assert.AreEqual("ExcludedEditionOverlap", result.Detail.Reason);
        var created = await rig.LoadWorkAsync(result.Work.Id);
        CollectionAssert.AreEqual(new[] { Isbn1 }, created.Editions.Select(edition => edition.Isbn13).ToArray(), "Only the compatible standard edition is written.");
        var after = await rig.CatalogRowsAsync();
        foreach (var row in before) CollectionAssert.Contains(after, row, "The held Work is byte-unchanged: no reference, edition or page length.");

        // The counter-case: the overlap is on the standard edition, so it is proof and only that edition attaches.
        rig.OpenLibrary.Records["y"] = Book("openlibrary", "y", "Dune", ["Frank Herbert"],
            [Edition("Dune", Isbn2, "Ebook"), Edition("Dune Study Guide", Isbn3, "Ebook")]);
        var attached = await rig.ResolveAsync("openlibrary", "y");
        Assert.AreEqual(CatalogResolutionOutcome.Attached, attached.Outcome);
        Assert.AreEqual(held, attached.Work.Id);
        var heldWork = await rig.LoadWorkAsync(held);
        CollectionAssert.AreEqual(new[] { Isbn2 }, heldWork.Editions.Select(edition => edition.Isbn13).ToArray(), "The study guide's ISBN never attached.");
    }

    [TestMethod]
    public async Task AMixedStandardAndAbridgedRecordOnlyOffersItsStandardIsbnToEveryConsumer()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["mixed"] = Book("demo", "mixed", "Dune", ["Frank Herbert"],
            [Edition("Dune", Isbn1, "Paperback"), Edition("Dune (Abridged Edition)", Isbn2, "Ebook"), Edition("Dune Study Guide", Isbn3, "Ebook")],
            description: null);

        var work = (await rig.ResolveAsync("demo", "mixed")).Work;

        await using var scope = rig.Host.Services.CreateAsyncScope();
        var lookup = await scope.ServiceProvider.GetRequiredService<IWorkLookup>().FindAsync(work.Id, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Isbn1 }, lookup!.Isbn13s.ToArray());
        Assert.IsEmpty(lookup.AlternateTitles ?? [], "The variant titles never became aliases.");
        var observation = (await rig.ObservationsAsync(work.Id)).Single();
        using var snapshot = System.Text.Json.JsonDocument.Parse(observation.Snapshot);
        var excluded = snapshot.RootElement.GetProperty("unattachedEditions").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(new[] { Isbn2, Isbn3 }, excluded.Select(edition => edition.GetProperty("isbn13").GetString()).ToArray());
        Assert.IsTrue(excluded.All(edition => edition.GetProperty("exclusion").GetString() == "Class"));
    }

    [TestMethod]
    public async Task AnEnglishRecordWhoseGermanEditionSharesAnEnglishWorksIsbnIsNoProof()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["en"] = Book("demo", "en", "The Hobbit", ["J. R. R. Tolkien"], [Edition("The Hobbit", Isbn1, "Paperback", "en")], language: "en");
        var english = (await rig.ResolveAsync("demo", "en")).Work;
        var before = await rig.CatalogRowsAsync();
        // An Open Library family: English editions plus a German one that happens to carry the English Work's ISBN.
        rig.OpenLibrary.Records["family"] = Book("openlibrary", "family", "The Hobbit", ["J. R. R. Tolkien"],
            [Edition("The Hobbit", Isbn2, "Ebook", "en"), Edition("Der kleine Hobbit", Isbn1, "Paperback", "de")], language: "en");

        var result = await rig.ResolveAsync("openlibrary", "family");

        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual(ObservationKind.NoProof, result.Detail!.Kind);
        CollectionAssert.AreEqual(new[] { Isbn2 }, (await rig.LoadWorkAsync(result.Work.Id)).Editions.Select(edition => edition.Isbn13).ToArray());
        var after = await rig.CatalogRowsAsync();
        foreach (var row in before) CollectionAssert.Contains(after, row);
        Assert.AreNotEqual(english.Id, result.Work.Id);
    }

    // ------------------------------------------------------------------------------------------------------------ legacy Works

    [TestMethod]
    public async Task ALegacyWorkWithUnknownClassAndLanguageIsOnlyEverBlockedNeverConfirmed()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        var legacy = await SeedLegacyWorkAsync(rig, "The Legacy Book", "Legacy Author", Isbn1);

        // A plain English record with proof attaches (today's de-facto assumption), and its language-tagged edition is recorded.
        rig.OpenLibrary.Records["ok"] = Book("openlibrary", "ok", "The Legacy Book", ["Legacy Author"],
            [Edition("The Legacy Book", Isbn1, "Paperback", "en"), Edition("The Legacy Book", Isbn2, "Ebook", "en")], language: "en");
        var attached = await rig.ResolveAsync("openlibrary", "ok");
        Assert.AreEqual(CatalogResolutionOutcome.Attached, attached.Outcome);
        Assert.AreEqual(legacy, attached.Work.Id);
        var work = await rig.LoadWorkAsync(legacy);
        Assert.IsNull(work.VersionClass, "A legacy class is never back-filled by an attach.");
        Assert.IsNull(work.Editions.Single(edition => edition.Isbn13 == Isbn1).Language, "A legacy edition keeps its unknown language.");
        Assert.AreEqual("en", work.Editions.Single(edition => edition.Isbn13 == Isbn2).Language);

        // A non-default language cannot be verified against a legacy Work, and a non-Text record cannot be confirmed either.
        rig.OpenLibrary.Records["de"] = Book("openlibrary", "de", "The Legacy Book", ["Legacy Author"], [Edition("The Legacy Book", Isbn1, "Paperback", "de")], language: "de");
        rig.OpenLibrary.Records["abridged"] = Book("openlibrary", "abridged", "The Legacy Book (Abridged)", ["Legacy Author"],
            [Edition("The Legacy Book (Abridged)", Isbn1, "Paperback", "en")], language: "en");
        // The first attach left an English edition, so a German record is now a known-language collision rather than unverifiable.
        Assert.AreEqual("Language", (await rig.ResolveAsync("openlibrary", "de")).Detail!.Reason);
        Assert.AreEqual("ClassUnverifiable", (await rig.ResolveAsync("openlibrary", "abridged")).Detail!.Reason);
    }

    [TestMethod]
    public async Task AGermanRecordAgainstALegacyWorkWithNoKnownLanguageIsUnverifiable()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        await SeedLegacyWorkAsync(rig, "Der Legacy", "Legacy Author", Isbn1);
        rig.OpenLibrary.Records["de"] = Book("openlibrary", "de", "Der Legacy", ["Legacy Author"], [Edition("Der Legacy", Isbn1, "Paperback", "de")], language: "de");

        var result = await rig.ResolveAsync("openlibrary", "de");

        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual("LanguageUnverifiable", result.Detail!.Reason);
    }

    // ------------------------------------------------------------------------------------------------------------ known reference

    [TestMethod]
    public async Task ARepeatedResolveOfAKnownReferenceMakesNoProviderCallAndWritesNothingAnywhere()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["b"] = Book("demo", "b", "Known Book", ["Known Author"], [Edition("Known Book", Isbn1)], [Series("Known Cycle", "1")]);
        await rig.ResolveAsync("demo", "b");
        var calls = rig.Demo.DetailCalls;
        var before = await rig.CatalogRowsAsync();

        // Even a changed record, a disabled provider and a later clock make no difference: a saved reference is never re-validated.
        rig.Demo.Records["b"] = rig.Demo.Records["b"] with { Description = "Changed.", Authors = ["Someone New"] };
        rig.Clock.Advance(TimeSpan.FromDays(30));
        for (var index = 0; index < 5; index++)
        {
            var again = await rig.ResolveAsync("demo", "b");
            Assert.IsFalse(again.WasCreated);
            Assert.AreEqual(CatalogResolutionOutcome.Existing, again.Outcome);
        }

        Assert.AreEqual(calls, rig.Demo.DetailCalls, "Zero provider calls.");
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync(), "Every row value, including last-seen times and versions, is unchanged in every catalog and evidence table.");
    }

    // ------------------------------------------------------------------------------------------------------------ ambiguous names (U1)

    [TestMethod]
    public async Task AnAmbiguousNameGetsAProvisionalAuthorAnUnboundSeriesClaimAndNoNamesakeGainsAnything()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var (olderAuthor, newerAuthor, olderSeries, newerSeries, follower) = await SeedNamesakesAsync(rig);
        var before = await rig.CatalogRowsAsync();
        var notices = await rig.DbAsync(database => database.NotificationEvents.CountAsync());
        rig.Demo.Records["amb"] = Book("demo", "amb", "A New Book", ["Sam Namesake"], [Edition("A New Book", Isbn1)], [Series("The Namesake Cycle", "3")]);

        var result = await rig.ResolveAsync("demo", "amb");

        var work = await rig.LoadWorkAsync(result.Work.Id);
        var credit = work.Authors.Single();
        Assert.AreNotEqual(olderAuthor, credit.AuthorId);
        Assert.AreNotEqual(newerAuthor, credit.AuthorId);
        Assert.AreEqual(AuthorBindingBasis.Provisional, credit.BindingBasis);
        Assert.AreEqual("Sam Namesake", credit.Author.CanonicalName, "The reported name is kept, bound to nobody.");
        Assert.IsEmpty(work.SeriesEntries, "An ambiguous series is never an entry.");
        var claims = await rig.ClaimsAsync(work.Id);
        var unbound = claims.Single(claim => claim.Field == MetadataClaimField.SeriesPosition);
        Assert.AreEqual("unbound:the namesake cycle", unbound.ScopeKey);
        Assert.AreEqual(notices, await rig.DbAsync(database => database.NotificationEvents.CountAsync()), "Provisional and unbound outcomes notify nobody.");
        Assert.AreEqual(2, await rig.DbAsync(database => database.Follows.CountAsync(item => item.UserId == follower)));

        // The namesakes' own rows and relationships are untouched.
        var after = await rig.CatalogRowsAsync();
        foreach (var row in before) CollectionAssert.Contains(after, row);
        Assert.AreEqual(0, await rig.DbAsync(database => database.Set<WorkAuthor>().CountAsync(item => item.WorkId == work.Id && (item.AuthorId == olderAuthor || item.AuthorId == newerAuthor))));
        Assert.AreEqual(0, await rig.DbAsync(database => database.SeriesEntries.CountAsync(item => item.WorkId == work.Id && (item.SeriesId == olderSeries || item.SeriesId == newerSeries))));

        // Observing the same record again never adds a second provisional author.
        rig.Demo.Records["amb2"] = rig.Demo.Records["amb"] with { ExternalId = "amb2", Editions = [] };
        var second = await rig.ResolveAsync("demo", "amb2");
        Assert.AreNotEqual(work.Id, second.Work.Id);
        Assert.AreEqual(1, (await rig.LoadWorkAsync(work.Id)).Authors.Count);
    }

    [TestMethod]
    public async Task ASourceAuthorIdSelectsExactlyOneNamesakeEvenWhenItIsNotTheOldest()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var (olderAuthor, newerAuthor, _, _, _) = await SeedNamesakesAsync(rig);
        await rig.DbAsync(async database =>
        {
            database.ExternalReferences.Add(new ExternalReference("demo", ExternalReferenceEntityType.Author, newerAuthor, "au-77", ObservationRig.Start));
            await Task.CompletedTask;
            return 0;
        });
        var record = Book("demo", "ref", "Reference Book", ["Sam Namesake"], [Edition("Reference Book", Isbn1)]);
        rig.Demo.Records["ref"] = record with { AuthorCredits = [new BookAuthorCredit("Sam Namesake", "Author", "au-77")] };

        var work = await rig.LoadWorkAsync((await rig.ResolveAsync("demo", "ref")).Work.Id);

        Assert.AreEqual(newerAuthor, work.Authors.Single().AuthorId, "Bound by the provider's own id, not by sorting first.");
        Assert.AreEqual(AuthorBindingBasis.Reference, work.Authors.Single().BindingBasis);
        Assert.AreEqual("Author", work.Authors.Single().Role);
        Assert.AreNotEqual(olderAuthor, work.Authors.Single().AuthorId);
    }

    [TestMethod]
    public async Task ANamesakeTiedToADifferentSourceIdIsAnotherPersonSoTheCreditGetsItsOwnAuthorWithItsSourceId()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var existing = await rig.DbAsync(async database =>
        {
            var author = new Author("Pat Person", null, ObservationRig.Start.AddDays(-50));
            database.Authors.Add(author);
            database.ExternalReferences.Add(new ExternalReference("demo", ExternalReferenceEntityType.Author, author.Id, "au-1", ObservationRig.Start));
            await Task.CompletedTask;
            return author.Id;
        });
        var record = Book("demo", "p", "Person Book", ["Pat Person"], [Edition("Person Book", Isbn1)]);
        rig.Demo.Records["p"] = record with { AuthorCredits = [new BookAuthorCredit("Pat Person", null, "au-2")] };

        var work = await rig.LoadWorkAsync((await rig.ResolveAsync("demo", "p")).Work.Id);

        var credit = work.Authors.Single();
        Assert.AreNotEqual(existing, credit.AuthorId, "The provider says this is a different person from the one it already identified as au-1.");
        Assert.AreEqual(AuthorBindingBasis.Created, credit.BindingBasis);
        Assert.AreEqual(1, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference =>
            reference.EntityType == ExternalReferenceEntityType.Author && reference.ExternalId == "au-2" && reference.EntityId == credit.AuthorId)),
            "The new author remembers the provider's own id, so the next record with it binds exactly.");
    }

    // ------------------------------------------------------------------------------------------------------------ notices

    [TestMethod]
    public async Task OnlyAnActualCreationNotifiesAndItNamesACatalogAdditionNotARelease()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        var follower = await FollowAuthorAsync(rig, "Ada Writer");
        rig.Demo.Records["n1"] = Book("demo", "n1", "Notice Book", ["Ada Writer"], [Edition("Notice Book", Isbn1)]);
        await rig.ResolveAsync("demo", "n1");

        var events = await rig.DbAsync(database => database.NotificationEvents.AsNoTracking().ToListAsync());

        var notice = events.Single(item => item.Category == NotificationCategories.AuthorNewWorkDetected);
        Assert.AreEqual(follower, notice.RecipientUserId);
        StringAssert.StartsWith(notice.Title, "Added to the catalog:");
        StringAssert.Contains(notice.Detail!, "may be an older title");

        // A second source attaching, and a replay, add nothing.
        rig.OpenLibrary.Records["n2"] = Book("openlibrary", "n2", "Notice Book", ["Ada Writer"], [Edition("Notice Book", Isbn1), Edition("Notice Book", Isbn2, "Paperback")]);
        await rig.ResolveAsync("openlibrary", "n2");
        await rig.ResolveAsync("demo", "n1");
        Assert.AreEqual(1, await rig.DbAsync(database => database.NotificationEvents.CountAsync()));
    }

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static async Task<Guid> SeedLegacyWorkAsync(ObservationRig rig, string title, string author, string isbn) =>
        await rig.DbAsync(async database =>
        {
            var credit = new Author(author, null, ObservationRig.Start.AddDays(-400));
            database.Authors.Add(credit);
            var work = new Work(title, null, null, null, PublicationStatus.Unknown, ObservationRig.Start.AddDays(-400));
            work.AddAuthor(credit, 0);
            work.AddEdition(new Edition(work.Id, title, EditionFormat.Paperback, isbn, null, ObservationRig.Start.AddDays(-400)));
            database.Works.Add(work);
            await Task.CompletedTask;
            return work.Id;
        });

    private static async Task<Guid> FollowAuthorAsync(ObservationRig rig, string name) =>
        await rig.DbAsync(async database =>
        {
            var reader = await database.Users.Select(user => user.Id).FirstAsync();
            var author = new Author(name, null, ObservationRig.Start.AddDays(-30));
            database.Authors.Add(author);
            database.Follows.Add(new Follow(reader, FollowSubjectType.Author, author.Id, ObservationRig.Start.AddDays(-20)));
            return reader;
        });

    private static async Task<(Guid OlderAuthor, Guid NewerAuthor, Guid OlderSeries, Guid NewerSeries, Guid Follower)> SeedNamesakesAsync(ObservationRig rig) =>
        await rig.DbAsync(async database =>
        {
            var reader = await database.Users.Select(user => user.Id).FirstAsync();
            var olderAuthor = new Author("Sam Namesake", null, ObservationRig.Start.AddDays(-100));
            var newerAuthor = new Author("Sam Namesake", null, ObservationRig.Start.AddDays(-10));
            var olderSeries = new Series("The Namesake Cycle", SeriesStatus.Unknown, ObservationRig.Start.AddDays(-100));
            var newerSeries = new Series("The Namesake Cycle", SeriesStatus.Unknown, ObservationRig.Start.AddDays(-10));
            database.Authors.AddRange(olderAuthor, newerAuthor);
            database.Series.AddRange(olderSeries, newerSeries);
            database.Follows.Add(new Follow(reader, FollowSubjectType.Author, olderAuthor.Id, ObservationRig.Start.AddDays(-50)));
            database.Follows.Add(new Follow(reader, FollowSubjectType.Series, newerSeries.Id, ObservationRig.Start.AddDays(-50)));
            return (olderAuthor.Id, newerAuthor.Id, olderSeries.Id, newerSeries.Id, reader);
        });
}
