using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FamilyLibrarian.Web.Tests.Harness.RigBooks;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Durable regressions for the Codex M11 review of the combined P2-B-E diff (V1-V8). Each case runs production code against real
/// PostgreSQL. The failing-before / passing-after status of every test is recorded in the Phase 2 plan, section 16.
/// </summary>
[TestClass]
public sealed class CatalogObservationCorrectionTests
{
    private const string EnglishIsbn = "9785000000001";
    private const string GermanIsbn = "9785000000018";
    private const string Isbn3 = "9785000000025";

    private static ProviderRefreshResult Step(WorkRefreshReport report, string provider, string phase = "Refresh") =>
        report.Providers.Single(step => step.ProviderId == provider && step.Phase == phase);

    // ------------------------------------------------------------------------------------------------------------ V1: tied languages

    [TestMethod]
    public async Task ATiedMixedLanguageRecordIsNeverAttachedToAnEnglishWorkAndNoTranslationJoinsAnyWork()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["en"] = Book("demo", "en", "Language Book", ["Lang Writer"], [Edition("Language Book", EnglishIsbn, "Ebook", "en")], language: "en");
        var english = (await rig.ResolveAsync("demo", "en")).Work.Id;
        rig.OpenLibrary.Records["family"] = Book("openlibrary", "family", "Language Book", ["Lang Writer"],
        [
            Edition("Language Book", EnglishIsbn, "Paperback", "en"),
            Edition("Sprachbuch", GermanIsbn, "Paperback", "de")
        ]);

        var result = await rig.ResolveAsync("openlibrary", "family");

        Assert.IsTrue(result.WasCreated, "The English ISBN of a family with no coherent language is no proof, so nothing is attached.");
        Assert.AreNotEqual(english, result.Work.Id);
        Assert.AreEqual(CatalogResolutionOutcome.Created, result.Outcome);
        var created = await rig.LoadWorkAsync(result.Work.Id);
        Assert.IsEmpty(created.Editions, "Neither translation is written as an Edition of the new Work.");
        CollectionAssert.AreEqual(new[] { EnglishIsbn }, (await rig.LoadWorkAsync(english)).Editions.Select(edition => edition.Isbn13).ToArray());
        Assert.AreEqual(0, await rig.DbAsync(database => database.Editions.CountAsync(edition => edition.Isbn13 == GermanIsbn)),
            "The German ISBN is held by no Work.");

        await using var scope = rig.Host.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IWorkLookup>();
        CollectionAssert.AreEqual(new[] { EnglishIsbn }, (await lookup.FindAsync(english, CancellationToken.None))!.Isbn13s.ToArray());
        Assert.IsEmpty((await lookup.FindAsync(created.Id, CancellationToken.None))!.Isbn13s, "The family's ISBNs reach no consumer's identity.");

        var snapshot = JsonDocument.Parse((await rig.ObservationsAsync(created.Id)).Single().Snapshot).RootElement;
        var unattached = snapshot.GetProperty("unattachedEditions").EnumerateArray().ToArray();
        Assert.HasCount(2, unattached, "Both translations are kept as evidence only.");
        Assert.IsTrue(unattached.All(edition => edition.GetProperty("exclusion").GetString() == "LanguageConflict"));
    }

    [TestMethod]
    public async Task ATiedMixedLanguageRecordCreatesOneWorkWithNoEditionsInsteadOfAMultilingualOne()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["family"] = Book("demo", "family", "Family Book", ["Fam Writer"],
        [
            Edition("Family Book", EnglishIsbn, "Ebook", "en"),
            Edition("Familienbuch", GermanIsbn, "Ebook", "de")
        ]);

        var work = await rig.LoadWorkAsync((await rig.ResolveAsync("demo", "family")).Work.Id);

        Assert.IsLessThanOrEqualTo(1, work.Editions.Select(edition => edition.Language).Where(language => language is not null).Distinct().Count(),
            "One Work never holds two languages.");
        Assert.IsEmpty(work.Editions);
    }

    [TestMethod]
    public async Task ATiedMixedLanguageRefreshNeverBroadensASavedWorkAndDoesNotCallItsOwnEditionContradicted()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Book("demo", "r1", "Language Book", ["Lang Writer"], [Edition("Language Book", EnglishIsbn, "Ebook", "en")], language: "en");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        rig.Demo.Records["r1"] = Book("demo", "r1", "Language Book", ["Lang Writer"],
        [
            Edition("Language Book", EnglishIsbn, "Ebook", "en"),
            Edition("Sprachbuch", GermanIsbn, "Ebook", "de")
        ]);

        var report = await rig.RefreshAsync(workId);

        CollectionAssert.AreEqual(new[] { EnglishIsbn }, (await rig.LoadWorkAsync(workId)).Editions.Select(edition => edition.Isbn13).ToArray());
        Assert.IsNull(Step(report, "demo").ContradictedIsbns, "The saved edition is not contradicted: the record simply states no coherent language.");
        await using var scope = rig.Host.Services.CreateAsyncScope();
        CollectionAssert.AreEqual(
            new[] { EnglishIsbn },
            (await scope.ServiceProvider.GetRequiredService<IWorkLookup>().FindAsync(workId, CancellationToken.None))!.Isbn13s.ToArray());
    }

    [TestMethod]
    public async Task ADiscoveredTiedMixedLanguageRecordIsNoProofAndIsNeverAttached()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["r1"] = Book("demo", "r1", "Language Book", ["Lang Writer"], [Edition("Language Book", EnglishIsbn, "Ebook", "en")], language: "en");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var family = Book("openlibrary", "family", "Language Book", ["Lang Writer"],
        [
            Edition("Language Book", EnglishIsbn, "Paperback", "en"),
            Edition("Sprachbuch", GermanIsbn, "Paperback", "de")
        ]);
        rig.OpenLibrary.Records["family"] = family;
        rig.OpenLibrary.SearchResults.Add(family);

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.NoProof, Step(report, "openlibrary", "Discover").Status);
        Assert.AreEqual(0, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference => reference.ExternalId == "family")));
        CollectionAssert.AreEqual(new[] { EnglishIsbn }, (await rig.LoadWorkAsync(workId)).Editions.Select(edition => edition.Isbn13).ToArray());
    }

    // ------------------------------------------------------------------------------------------------------------ V3: complete claim scopes

    private static BookCandidate DescribedBook(string description) =>
        Book("demo", "r1", "Big History Book", ["Hist Writer"], [Edition("Big History Book", EnglishIsbn, "Ebook", "en")], null, description,
            new DateOnly(1990, 1, 1), DatePrecision.Year, DateBasis.Original, "en");

    [TestMethod]
    public async Task ARefreshDecidesFromEveryClaimOfTheFactEvenWhenTheWorkHoldsMoreThanTwoThousandRows()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = DescribedBook("Initial");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        await rig.SeedDescriptionHistoryAsync(workId, sources: 201, perSource: 10);
        Assert.IsGreaterThan(2_000, (await rig.ClaimsAsync(workId)).Count);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        rig.Demo.Records["r1"] = DescribedBook("Updated source value");

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(report, "demo").Status);
        Assert.AreEqual("Updated source value", (await rig.LoadWorkAsync(workId)).Description,
            "The source that supplied the selected value may change its statement, however many other rows the Work holds.");
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(201 * 10 + 2, descriptions, "Nothing was dropped, duplicated or compacted away.");
        foreach (var source in descriptions.GroupBy(claim => (claim.ProviderId, claim.SourceEntityId)))
        {
            Assert.AreEqual(1, source.Count(claim => claim.IsCurrent), $"Exactly one current statement per source: {source.Key}.");
        }

        var demo = descriptions.Where(claim => claim.ProviderId == "demo").ToArray();
        Assert.HasCount(2, demo);
        Assert.IsTrue(demo.Single(claim => claim.IsCurrent).IsSelected);
        Assert.AreEqual(1, descriptions.Count(claim => claim.IsSelected), "One selected claim for the fact.");

        // Replay: an identical refresh changes nothing but last-seen.
        var before = await rig.CatalogRowsAsync("last_seen_at");
        rig.Clock.Advance(TimeSpan.FromDays(1));
        var again = await rig.RefreshAsync(workId);
        Assert.AreEqual(ProviderRefreshStatus.Unchanged, Step(again, "demo").Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync("last_seen_at"));
    }

    // ------------------------------------------------------------------------------------------------------------ V4: unbound membership

    private static async Task<(Guid Older, Guid Newer, Guid Follower)> SeedTwinSeriesAsync(ObservationRig rig)
    {
        return await rig.DbAsync(async database =>
        {
            var older = new Series("Twin Cycle", SeriesStatus.Unknown, ObservationRig.Start.AddDays(-90));
            var newer = new Series("Twin Cycle", SeriesStatus.Unknown, ObservationRig.Start.AddDays(-30));
            database.Series.AddRange(older, newer);
            var user = await database.Users.Select(item => item.Id).FirstAsync();
            database.Follows.Add(new Follow(user, FollowSubjectType.Series, older.Id, ObservationRig.Start));
            database.Follows.Add(new Follow(user, FollowSubjectType.Series, newer.Id, ObservationRig.Start));
            return (older.Id, newer.Id, user);
        });
    }

    [TestMethod]
    public async Task AnAmbiguousSeriesMembershipWithoutAPositionStillHasADurableUnboundClaimAndNoInventedNumber()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await SeedTwinSeriesAsync(rig);
        rig.Demo.Records["u1"] = Book("demo", "u1", "Unnumbered Book", ["Una Writer"], [Edition("Unnumbered Book", EnglishIsbn, "Ebook", "en")], [Series("Twin Cycle", null)]);

        var work = await rig.LoadWorkAsync((await rig.ResolveAsync("demo", "u1")).Work.Id);

        Assert.IsEmpty(work.SeriesEntries, "Never attached to a guessed namesake.");
        var claim = (await rig.ClaimsAsync(work.Id)).Single(item => item.Field == MetadataClaimField.SeriesPosition);
        Assert.AreEqual("unbound:twin cycle", claim.ScopeKey);
        Assert.IsTrue(claim.IsCurrent);
        using var value = JsonDocument.Parse(claim.ValueJson);
        Assert.AreEqual(JsonValueKind.Null, value.RootElement.GetProperty("label").ValueKind, "No position was invented.");
        Assert.AreEqual(JsonValueKind.Null, value.RootElement.GetProperty("sort").ValueKind);
        Assert.AreEqual("Twin Cycle", value.RootElement.GetProperty("seriesLabel").GetString());

        // Repeating the observation keeps the one claim and changes nothing else.
        var before = await rig.CatalogRowsAsync("last_seen_at");
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.AreEqual(ProviderRefreshStatus.Unchanged, Step(await rig.RefreshAsync(work.Id), "demo").Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync("last_seen_at"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnUnboundMembershipThatASourceIdLaterBindsStopsBeingCurrentButItsEvidenceStays(bool withPosition)
    {
        await using var rig = await ObservationRig.CreateAsync();
        var (older, newer, _) = await SeedTwinSeriesAsync(rig);
        var label = withPosition ? "3" : null;
        rig.Demo.Records["u1"] = Book("demo", "u1", "Bound Later Book", ["Una Writer"], [Edition("Bound Later Book", EnglishIsbn, "Ebook", "en")], [Series("Twin Cycle", label)]);
        var workId = (await rig.ResolveAsync("demo", "u1")).Work.Id;
        Assert.AreEqual(1, (await rig.ClaimsAsync(workId)).Count(claim => claim.ScopeKey.StartsWith("unbound:", StringComparison.Ordinal) && claim.IsCurrent));
        var notices = await rig.DbAsync(database => database.NotificationEvents.CountAsync());

        // The provider now identifies the series by its own id, which is recorded against the newer namesake.
        await rig.DbAsync(async database =>
        {
            database.ExternalReferences.Add(new ExternalReference("demo", ExternalReferenceEntityType.Series, newer, "ser-9", ObservationRig.Start));
            await Task.CompletedTask;
            return 0;
        });
        rig.Clock.Advance(TimeSpan.FromDays(2));
        rig.Demo.Records["u1"] = rig.Demo.Records["u1"] with { Series = [Series("Twin Cycle", label, sourceId: "ser-9")] };

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(report, "demo").Status);
        var work = await rig.LoadWorkAsync(workId);
        var entry = work.SeriesEntries.Single();
        Assert.AreEqual(newer, entry.SeriesId, "Bound by the provider's own id, never by sorting first.");
        Assert.AreEqual(SeriesBindingBasis.Reference, entry.BindingBasis);
        Assert.AreEqual(0, await rig.DbAsync(database => database.SeriesEntries.CountAsync(item => item.SeriesId == older)));
        var claims = await rig.ClaimsAsync(workId);
        var unbound = claims.Where(claim => claim.ScopeKey.StartsWith("unbound:", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, unbound, "The historical unbound statement is kept as evidence.");
        Assert.IsFalse(unbound.Single().IsCurrent, "It is no longer an unresolved membership.");
        Assert.AreEqual(withPosition ? 1 : 0, claims.Count(claim => claim.ScopeKey == newer.ToString("D") && claim.IsCurrent));
        Assert.AreEqual(notices, await rig.DbAsync(database => database.NotificationEvents.CountAsync()), "Binding on refresh notifies no author or series follower.");

        await using (var scope = rig.Host.Services.CreateAsyncScope())
        {
            var page = await scope.ServiceProvider.GetRequiredService<CatalogEvidenceInspector>()
                .ListIssuesAsync(EvidenceIssueCategory.UnboundSeriesClaims, null, 50, CancellationToken.None);
            Assert.IsEmpty(page.Items, "The report no longer lists a resolved membership.");
        }

        var settled = await rig.CatalogRowsAsync("last_seen_at");
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.AreEqual(ProviderRefreshStatus.Unchanged, Step(await rig.RefreshAsync(workId), "demo").Status, "A repeat is idempotent.");
        CollectionAssert.AreEqual(settled, await rig.CatalogRowsAsync("last_seen_at"));
    }

    // ------------------------------------------------------------------------------------------------------------ V6: persisted byte budgets

    [TestMethod]
    public async Task AMultibyteDescriptionIsStoredWithinTheSixteenKibibyteClaimBudgetAsAFlaggedPrefix()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var original = new string('界', 12_000);
        rig.Demo.Records["r1"] = DescribedBook(original);

        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;

        var claim = (await rig.ClaimsAsync(workId)).Single(item => item.Field == MetadataClaimField.Description);
        Assert.IsLessThanOrEqualTo(16 * 1024, Encoding.UTF8.GetByteCount(claim.ValueJson), "The persisted value respects the byte budget.");
        using var json = JsonDocument.Parse(claim.ValueJson);
        Assert.IsTrue(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.IsTrue(original.StartsWith(json.RootElement.GetProperty("text").GetString()!, StringComparison.Ordinal));
        var size = await rig.DbAsync(async database =>
            await database.Database.SqlQuery<int>($"SELECT octet_length(value_json::text) AS \"Value\" FROM catalog.metadata_claims WHERE id = {claim.Id}")
                .SingleAsync());
        Assert.IsLessThanOrEqualTo(16 * 1024 + 256, size, "jsonb's own rendering is no larger than the budget plus its spacing.");
    }

    // ------------------------------------------------------------------------------------------------------------ V8: legacy date precision

    private static async Task<Guid> WorkWithLegacyDateAsync(ObservationRig rig)
    {
        rig.Demo.Records["r1"] = Book("demo", "r1", "Legacy Date Book", ["Date Writer"], [Edition("Legacy Date Book", EnglishIsbn, "Ebook", "en")], date: null, language: "en");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        await rig.DbAsync(async database =>
        {
            await database.Database.ExecuteSqlRawAsync(
                "UPDATE catalog.works SET first_publication_date = '1990-01-01', first_publication_precision = NULL, first_publication_basis = NULL WHERE id = {0}", workId);
            return 0;
        });
        return workId;
    }

    [TestMethod]
    public async Task AnEditionDerivedFallbackNeverGivesALegacyWorkDateADayPrecisionOrABasis()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var workId = await WorkWithLegacyDateAsync(rig);
        var statusBefore = (await rig.LoadWorkAsync(workId)).PublicationStatus;
        rig.Clock.Advance(TimeSpan.FromDays(3));
        rig.Demo.Records["r1"] = rig.Demo.Records["r1"] with
        {
            PublicationDate = new DateOnly(2010, 6, 15), PublicationDatePrecision = DatePrecision.Day, PublicationDateBasis = DateBasis.EditionDerived
        };

        var report = await rig.RefreshAsync(workId);

        var work = await rig.LoadWorkAsync(workId);
        Assert.AreEqual(new DateOnly(1990, 1, 1), work.FirstPublicationDate, "The old date stays the shown value.");
        Assert.IsNull(work.FirstPublicationPrecision, "NULL is not evidence of an exact day.");
        Assert.IsNull(work.FirstPublicationBasis);
        Assert.AreEqual(statusBefore, work.PublicationStatus);
        var claims = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.OriginalPublicationDate).ToArray();
        Assert.HasCount(2, claims, "The fallback is kept as evidence next to the unattributed legacy value.");
        Assert.AreEqual("legacy-unattributed", claims.Single(claim => claim.IsSelected).ProviderId);
        Assert.IsNotNull(Step(report, "demo"));

        // Repeating it changes nothing.
        var before = await rig.CatalogRowsAsync("last_seen_at");
        await rig.RefreshAsync(workId);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync("last_seen_at"));
    }

    [TestMethod]
    public async Task GenuineOriginalEvidenceStillSupersedesALegacyDateWithItsRealPrecisionAndBasis()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var workId = await WorkWithLegacyDateAsync(rig);
        rig.Demo.Records["r1"] = rig.Demo.Records["r1"] with
        {
            PublicationDate = new DateOnly(1990, 5, 12), PublicationDatePrecision = DatePrecision.Day, PublicationDateBasis = DateBasis.Original
        };

        await rig.RefreshAsync(workId);

        var work = await rig.LoadWorkAsync(workId);
        Assert.AreEqual(new DateOnly(1990, 5, 12), work.FirstPublicationDate);
        Assert.AreEqual(DatePrecision.Day, work.FirstPublicationPrecision);
        Assert.AreEqual(DateBasis.Original, work.FirstPublicationBasis);
    }

    [TestMethod]
    public async Task AYearOnlyOriginalClaimReplacesAYearNormalizedLegacyDateWithYearPrecision()
    {
        await using var rig = await ObservationRig.CreateAsync();
        var workId = await WorkWithLegacyDateAsync(rig);
        rig.Demo.Records["r1"] = rig.Demo.Records["r1"] with
        {
            PublicationDate = new DateOnly(1990, 1, 1), PublicationDatePrecision = DatePrecision.Year, PublicationDateBasis = DateBasis.Original
        };

        await rig.RefreshAsync(workId);

        var work = await rig.LoadWorkAsync(workId);
        Assert.AreEqual(new DateOnly(1990, 1, 1), work.FirstPublicationDate);
        Assert.AreEqual(DatePrecision.Year, work.FirstPublicationPrecision, "A genuine year-only statement says exactly that.");
        Assert.AreEqual(DateBasis.Original, work.FirstPublicationBasis);
    }

    // ------------------------------------------------------------------------------------------------------------ V7: the deadline covers writes

    private static async Task<long> WaitingForTheCatalogLockAsync(string connection)
    {
        await using var data = new NpgsqlConnection(connection);
        await data.OpenAsync();
        await using var command = data.CreateCommand();
        // pg_locks spans the whole server, and other tests run in their own databases at the same time: count this database only.
        command.CommandText = "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = 831705 AND NOT granted " +
            "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static BookCandidate Described(string provider, string id, string description) =>
        Book(provider, id, "Deadline Book", ["Dead Writer"], [Edition("Deadline Book", EnglishIsbn, "Ebook", "en")], null, description, null, null, null, "en");

    [TestMethod]
    public async Task TheOverallDeadlineEndsTheWaitForTheCatalogLockAndNothingCommitsAfterTheReport()
    {
        await using var rig = await ObservationRig.CreateAsync(refreshDeadline: TimeSpan.FromMilliseconds(300));
        rig.Demo.Records["r1"] = Described("demo", "r1", "Initial");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        rig.Demo.Records["r1"] = Described("demo", "r1", "Changed after the deadline.");
        var before = await rig.CatalogRowsAsync();
        await using var probe = new CatalogLockProbe(rig.Connection);
        await probe.HoldAsync(CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var report = await rig.RefreshAsync(workId);
        watch.Stop();

        Assert.IsLessThan(3_000, watch.ElapsedMilliseconds, "The 300 ms deadline, not the 5 s lock bound, ended the wait.");
        Assert.AreEqual(WorkRefreshStatus.Partial, report.Status);
        Assert.AreEqual(ProviderRefreshStatus.DeadlineReached, Step(report, "demo").Status);
        Assert.AreEqual(0, await WaitingForTheCatalogLockAsync(rig.Connection), "No waiter is left to write in the background.");
        await probe.ReleaseAsync(CancellationToken.None);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync(), "Nothing was written, before or after the lock was released.");
        Assert.AreEqual(2, rig.Demo.DetailCalls, "The fetch was made once; nothing was retried.");
    }

    [TestMethod]
    public async Task ADeadlineThatEndsALaterObservationKeepsTheEarlierCommittedOneAndReportsPartial()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true, refreshDeadline: TimeSpan.FromSeconds(2));
        rig.Demo.Records["r1"] = Described("demo", "r1", "Initial");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        rig.OpenLibrary.Records["ol"] = Described("openlibrary", "ol", "Open initial");
        Assert.AreEqual(workId, (await rig.ResolveAsync("openlibrary", "ol")).Work.Id);
        rig.Demo.Records["r1"] = Described("demo", "r1", "Demo changed.");
        rig.OpenLibrary.Records["ol"] = Described("openlibrary", "ol", "Open changed.");
        await using var probe = new CatalogLockProbe(rig.Connection);
        rig.OpenLibrary.OnDetails = async _ => await probe.HoldAsync(CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var report = await rig.RefreshAsync(workId);
        watch.Stop();

        Assert.IsLessThan(4_500, watch.ElapsedMilliseconds);
        Assert.AreEqual(WorkRefreshStatus.Partial, report.Status);
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(report, "demo").Status, "The first provider's observation had already committed.");
        Assert.AreEqual(ProviderRefreshStatus.DeadlineReached, Step(report, "openlibrary").Status);
        Assert.AreEqual(0, await WaitingForTheCatalogLockAsync(rig.Connection));
        await probe.ReleaseAsync(CancellationToken.None);
        var descriptions = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.IsTrue(descriptions.Any(claim => claim.ProviderId == "demo" && claim.IsCurrent && claim.ValueJson.Contains("Demo changed.", StringComparison.Ordinal)));
        Assert.IsFalse(descriptions.Any(claim => claim.ValueJson.Contains("Open changed.", StringComparison.Ordinal)), "The late observation wrote nothing.");

        // Nothing was lost: a later refresh completes it.
        rig.OpenLibrary.OnDetails = null;
        var later = await rig.RefreshAsync(workId);
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(later, "openlibrary").Status);
    }

    [TestMethod]
    public async Task ACallersOwnCancellationDuringTheLockWaitIsStillCancellationNotADeadline()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Described("demo", "r1", "Initial");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        rig.Demo.Records["r1"] = Described("demo", "r1", "Changed.");
        var before = await rig.CatalogRowsAsync();
        await using var probe = new CatalogLockProbe(rig.Connection);
        await probe.HoldAsync(CancellationToken.None);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await rig.RefreshAsync(workId, caller.Token));

        await probe.ReleaseAsync(CancellationToken.None);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync());
    }
}
