using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FamilyLibrarian.Web.Tests.Harness.RigBooks;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The administrator refresh and discovery, and the evidence they keep, on real PostgreSQL: a known reference's fresh record is
/// validated, never trusted because its id matched; unchanged is exactly that; a contradiction keeps only bounded evidence;
/// discovery attaches only proof; retention keeps what a selected value needs.
/// </summary>
[TestClass]
public sealed class CatalogObservationRefreshTests
{
    private const string Isbn1 = "9782000000001";
    private const string Isbn2 = "9782000000018";
    private const string Isbn3 = "9782000000025";

    private static BookCandidate Base(string externalId = "r1", string title = "Refresh Book", string description = "First description.") =>
        Book("demo", externalId, title, ["Rae Writer"], [Edition(title, Isbn1, "Ebook", "en")], [Series("Refresh Cycle", "1")], description,
            new DateOnly(1990, 1, 1), DatePrecision.Year, DateBasis.Original, "en", null, 200);

    private static ProviderRefreshResult Step(WorkRefreshReport report, string provider, string phase = "Refresh") =>
        report.Providers.Single(step => step.ProviderId == provider && step.Phase == phase);

    // ------------------------------------------------------------------------------------------------------------ refreshed / unchanged

    [TestMethod]
    public async Task ARefreshThatLearnsSomethingPersistsItAndAnIdenticalOneChangesOnlyLastSeen()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var noticesBefore = await rig.DbAsync(database => database.NotificationEvents.CountAsync());

        // The same source now reports a new edition, a changed description and page count, and another series position.
        rig.Clock.Advance(TimeSpan.FromDays(10));
        rig.Demo.Records["r1"] = Base(description: "Second description.") with
        {
            Editions = [Edition("Refresh Book", Isbn1, "Ebook", "en"), Edition("Refresh Book", Isbn2, "Paperback", "en", "New Press")],
            Series = [Series("Refresh Cycle", "2")],
            PageCount = 250
        };
        var first = await rig.RefreshAsync(workId);

        Assert.AreEqual(WorkRefreshStatus.Completed, first.Status);
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(first, "demo").Status);
        var work = await rig.LoadWorkAsync(workId);
        Assert.AreEqual("Second description.", work.Description, "The source that supplied the selected value may change its own statement.");
        Assert.AreEqual(250, work.CatalogPageCount);
        Assert.HasCount(2, work.Editions);
        Assert.AreEqual(EditionIdentityBasis.Reference, work.Editions.Single(edition => edition.Isbn13 == Isbn2).IdentityBasis);
        Assert.AreEqual("2", work.SeriesEntries.Single().PositionLabel);
        Assert.AreEqual(SeriesPositionState.SingleSource, work.SeriesEntries.Single().PositionState);
        var claims = await rig.ClaimsAsync(workId);
        var descriptions = claims.Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, descriptions, "The earlier statement is kept as history, not overwritten.");
        Assert.IsTrue(descriptions.Single(claim => claim.IsCurrent).IsSelected);
        Assert.IsFalse(descriptions.Single(claim => !claim.IsCurrent).IsSelected);
        Assert.HasCount(2, await rig.ObservationsAsync(workId));
        Assert.AreEqual(noticesBefore, await rig.DbAsync(database => database.NotificationEvents.CountAsync()), "A refresh never notifies.");
        Assert.AreEqual(1, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference => reference.EntityId == workId)));

        // An identical refresh is Unchanged: every logical value is identical, and only last-seen moves.
        var before = await rig.CatalogRowsAsync("last_seen_at");
        rig.Clock.Advance(TimeSpan.FromDays(1));
        var second = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.Unchanged, Step(second, "demo").Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync("last_seen_at"));
        var seen = (await rig.ObservationsAsync(workId)).OrderByDescending(observation => observation.ObservedAtUtc).First().LastSeenAtUtc;
        Assert.AreEqual(rig.Clock.UtcNow, seen, "The matching snapshot's last-seen advanced.");
        Assert.IsTrue((await rig.ClaimsAsync(workId)).Where(claim => claim.IsCurrent && claim.ProviderId == "demo")
            .All(claim => claim.LastSeenAtUtc == rig.Clock.UtcNow), "Every current claim the snapshot restates advanced its last-seen.");
    }

    // ------------------------------------------------------------------------------------------------------------ contradiction (U3-3)

    [TestMethod]
    [DataRow("Language")]
    [DataRow("Class")]
    [DataRow("Author")]
    [DataRow("Title")]
    [DataRow("Year")]
    public async Task AKnownReferencesRecordThatNowContradictsItsWorkKeepsOnlyEvidence(string reason)
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var rowsBefore = await rig.CatalogRowsAsync("last_seen_at");

        rig.Demo.Records["r1"] = reason switch
        {
            "Language" => Base() with { Language = "de", Editions = [Edition("Refresh Book", Isbn3, "Ebook", "de")] },
            "Class" => Base(title: "Refresh Book (Abridged)") with { Editions = [Edition("Refresh Book (Abridged)", Isbn3, "Ebook", "en")] },
            "Author" => Base() with { Authors = ["Entirely Else"], Editions = [Edition("Refresh Book", Isbn3, "Ebook", "en")] },
            "Title" => Base(title: "A Wholly Different Book") with { Editions = [Edition("A Wholly Different Book", Isbn3, "Ebook", "en")] },
            _ => Base() with { PublicationDate = new DateOnly(2018, 1, 1), Editions = [Edition("Refresh Book", Isbn3, "Ebook", "en")] }
        };
        rig.Clock.Advance(TimeSpan.FromDays(3));

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.Contradiction, Step(report, "demo").Status);
        Assert.AreEqual(reason, Step(report, "demo").Reason);
        var work = await rig.LoadWorkAsync(workId);
        CollectionAssert.AreEqual(new[] { Isbn1 }, work.Editions.Select(edition => edition.Isbn13).ToArray(), "No foreign or abridged ISBN was widened onto the Work.");
        Assert.AreEqual(1, await rig.DbAsync(database => database.ExternalReferences.CountAsync(item => item.EntityId == workId)), "The reference stays on its Work.");
        Assert.AreEqual(1, await rig.DbAsync(database => database.Works.CountAsync()), "No Work was created.");
        var claimsAfter = (await rig.CatalogRowsAsync("last_seen_at")).Where(row => row.StartsWith("metadata_claims|", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(rowsBefore.Where(row => row.StartsWith("metadata_claims|", StringComparison.Ordinal)).ToArray(), claimsAfter, "No claim changed.");

        var observations = await rig.ObservationsAsync(workId);
        Assert.HasCount(2, observations, "Only a bounded evidence snapshot was added.");
        using (var snapshot = System.Text.Json.JsonDocument.Parse(observations[1].Snapshot))
        {
            Assert.AreEqual("Contradiction", snapshot.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual(reason, snapshot.RootElement.GetProperty("reason").GetString());
        }

        // Repeating it adds no second snapshot; only that snapshot's last-seen moves.
        rig.Clock.Advance(TimeSpan.FromDays(1));
        await rig.RefreshAsync(workId);
        var again = await rig.ObservationsAsync(workId);
        Assert.HasCount(2, again);
        Assert.AreEqual(rig.Clock.UtcNow, again[1].LastSeenAtUtc);

        await using var scope = rig.Host.Services.CreateAsyncScope();
        var lookup = await scope.ServiceProvider.GetRequiredService<IWorkLookup>().FindAsync(workId, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Isbn1 }, lookup!.Isbn13s.ToArray());
    }

    // ------------------------------------------------------------------------------------------------------------ retired / absent / failing

    [TestMethod]
    public async Task ARefreshWhoseWorkIsRetiredWhileTheProviderWasAskedWritesNothing()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        rig.Demo.Records["r1"] = Base(description: "Changed after retirement.");
        rig.Demo.OnDetails = async _ => await rig.DbAsync(async database =>
        {
            await database.Database.ExecuteSqlRawAsync("UPDATE catalog.works SET is_retired = true WHERE id = {0}", workId);
            return 0;
        });
        var before = await rig.CatalogRowsAsync("is_retired");

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.WorkRetired, Step(report, "demo").Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync("is_retired"), "The fetch that started before retirement cannot write to the retired Work.");

        // A Work that is already retired is not refreshed at all, and an unknown Work is not found.
        rig.Demo.OnDetails = null;
        Assert.AreEqual(WorkRefreshStatus.WorkRetired, (await rig.RefreshAsync(workId)).Status);
        Assert.AreEqual(WorkRefreshStatus.WorkNotFound, (await rig.RefreshAsync(Guid.NewGuid())).Status);
    }

    [TestMethod]
    public async Task AMissingFailingOrCancelledProviderWritesNothingAndRemovesNothing()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var before = await rig.CatalogRowsAsync();

        // The record is gone: absence is staleness, not a deletion instruction.
        rig.Demo.Records.Clear();
        Assert.AreEqual(ProviderRefreshStatus.NotFound, Step(await rig.RefreshAsync(workId), "demo").Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync());

        rig.Demo.Records["r1"] = Base(description: "Would change.");
        rig.Demo.DetailFailure = new HttpRequestException("boom");
        var failed = await rig.RefreshAsync(workId);
        Assert.AreEqual(ProviderRefreshStatus.ProviderUnavailable, Step(failed, "demo").Status);
        Assert.AreEqual(WorkRefreshStatus.Partial, failed.Status);
        rig.Demo.DetailFailure = new System.Text.Json.JsonException("bad payload");
        Assert.AreEqual(ProviderRefreshStatus.ProviderUnavailable, Step(await rig.RefreshAsync(workId), "demo").Status);
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync());

        // The caller's own cancellation is not swallowed.
        rig.Demo.DetailFailure = null;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await rig.RefreshAsync(workId, cancelled.Token));
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync());
    }

    [TestMethod]
    public async Task ADisabledProviderIsNotAskedAndKeepsEveryFactItSupplied()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.OpenLibrary.Records["ol"] = Book("openlibrary", "ol", "Lifecycle Book", ["Life Writer"], [Edition("Lifecycle Book", Isbn1)], description: "OL words.");
        var workId = (await rig.ResolveAsync("openlibrary", "ol")).Work.Id;
        await rig.DisableAsync("openlibrary");
        var before = await rig.CatalogRowsAsync();
        var calls = rig.OpenLibrary.DetailCalls;

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(ProviderRefreshStatus.ProviderNotActive, Step(report, "openlibrary").Status);
        Assert.AreEqual(calls, rig.OpenLibrary.DetailCalls, "A disabled provider is not called.");
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync(), "Disabling deletes no reference, claim or snapshot.");
        await using var scope = rig.Host.Services.CreateAsyncScope();
        Assert.AreEqual(workId, (await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>().ResolveAsync("openlibrary", "ol", CancellationToken.None)).Work.Id,
            "The saved reference still resolves with the provider off.");

        // Re-enabled: the same reference is found, so no duplicate Work appears, and the next refresh just adds claims.
        await rig.EnableAsync("openlibrary");
        Assert.AreEqual(workId, (await rig.ResolveAsync("openlibrary", "ol")).Work.Id);
        Assert.AreEqual(1, await rig.DbAsync(database => database.Works.CountAsync()));
        Assert.AreEqual(ProviderRefreshStatus.Unchanged, Step(await rig.RefreshAsync(workId), "openlibrary").Status);
    }

    // ------------------------------------------------------------------------------------------------------------ ownership (U3-5)

    [TestMethod]
    public async Task ARefreshedRecordNeverMovesAnIsbnAnotherWorkOwnsAndReportsContradictedPersistedEditions()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base();
        rig.Demo.Records["r2"] = Book("demo", "r2", "Another Book", ["Other Writer"], [Edition("Another Book", Isbn2, "Ebook", "en")], language: "en");
        var first = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var other = (await rig.ResolveAsync("demo", "r2")).Work.Id;

        // r1 now also lists r2's ISBN, and re-describes its own persisted edition as German.
        rig.Demo.Records["r1"] = Base() with
        {
            Editions = [Edition("Refresh Book", Isbn1, "Ebook", "de"), Edition("Refresh Book", Isbn2, "Ebook", "en"), Edition("Refresh Book", Isbn3, "Ebook", "en")]
        };
        var report = await rig.RefreshAsync(first);

        var step = Step(report, "demo");
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, step.Status);
        CollectionAssert.AreEqual(new[] { Isbn1 }, step.ContradictedIsbns!.ToArray(), "A persisted edition now contradicted is reported.");
        var firstWork = await rig.LoadWorkAsync(first);
        CollectionAssert.AreEquivalent(new[] { Isbn1, Isbn3 }, firstWork.Editions.Select(edition => edition.Isbn13).ToArray(),
            "The contradicted edition is not deleted; the owned ISBN is not taken; the free one is added.");
        CollectionAssert.AreEqual(new[] { Isbn2 }, (await rig.LoadWorkAsync(other)).Editions.Select(edition => edition.Isbn13).ToArray());
        Assert.AreEqual(1, await rig.DbAsync(database => database.Editions.CountAsync(edition => edition.Isbn13 == Isbn2)));
    }

    // ------------------------------------------------------------------------------------------------------------ discovery

    [TestMethod]
    public async Task DiscoveryAttachesOnlyProofForThisWorkAndReportsEveryOtherPlausibleRecordWithoutKeepingIt()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var referencesBefore = await rig.DbAsync(database => database.ExternalReferences.CountAsync());
        var worksBefore = await rig.DbAsync(database => database.Works.CountAsync());

        // Five search results: one sharing the Work's ISBN, a same-title record with another author, and plain same-title records.
        BookCandidate Candidate(string id, string[] authors, string? isbn) =>
            Book("openlibrary", id, "Refresh Book", authors, isbn is null ? [] : [Edition("Refresh Book", isbn, "Ebook", "en")], language: "en");
        var proof = Candidate("c1", ["Rae Writer"], Isbn1) with { Description = "From Open Library.", PageCount = 300 };
        rig.OpenLibrary.SearchResults.AddRange([proof, Candidate("c2", ["Another Author"], null), Candidate("c3", ["Rae Writer"], null), Candidate("c4", ["Rae Writer"], null), Candidate("c5", ["Rae Writer"], null)]);
        foreach (var candidate in rig.OpenLibrary.SearchResults) rig.OpenLibrary.Records[candidate.ExternalId] = candidate;

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(1, rig.OpenLibrary.SearchCalls, "At most one search per provider without a reference.");
        var discovered = report.Providers.Where(step => step.Phase == "Discover").ToArray();
        Assert.AreEqual(ProviderRefreshStatus.Attached, discovered.Single().Status, "The proof-bearing record attaches and stops this provider's discovery.");
        Assert.AreEqual("c1", discovered.Single().ExternalId);
        Assert.AreEqual(1, rig.OpenLibrary.DetailCalls);
        Assert.AreEqual(referencesBefore + 1, await rig.DbAsync(database => database.ExternalReferences.CountAsync()));
        Assert.AreEqual(worksBefore, await rig.DbAsync(database => database.Works.CountAsync()), "No placeholder Work.");
        var work = await rig.LoadWorkAsync(workId);
        Assert.HasCount(1, work.Authors, "Attach never adds authors.");
        Assert.AreEqual(200, work.CatalogPageCount, "A sticky scalar already supplied by the first source is not replaced by another source.");
        var pages = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.PageCount).ToArray();
        Assert.HasCount(2, pages);
        Assert.AreEqual("openlibrary", pages.Single(claim => !claim.IsSelected).ProviderId, "The other source's value is kept as an attributed claim.");
        Assert.AreEqual(EditionIdentityBasis.Created, work.Editions.Single().IdentityBasis, "The existing edition is not rewritten.");
    }

    [TestMethod]
    public async Task WhenNothingProvesAMatchDiscoveryFetchesAtMostThreeDetailsAndKeepsNoneOfThem()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var before = await rig.CatalogRowsAsync();
        BookCandidate Candidate(string id, string[] authors) => Book("openlibrary", id, "Refresh Book", authors, [], language: "en");
        rig.OpenLibrary.SearchResults.AddRange(
        [
            Candidate("c1", ["Rae Writer"]), Candidate("c2", ["Another Author"]), Candidate("c3", ["Rae Writer"]),
            Candidate("c4", ["Rae Writer"]), Candidate("c5", ["Rae Writer"])
        ]);
        foreach (var candidate in rig.OpenLibrary.SearchResults) rig.OpenLibrary.Records[candidate.ExternalId] = candidate;

        var report = await rig.RefreshAsync(workId);

        Assert.AreEqual(1, rig.OpenLibrary.SearchCalls);
        Assert.AreEqual(3, rig.OpenLibrary.DetailCalls, "Three detail fetches at most.");
        Assert.AreEqual(2, report.Proposals.Count, "The two same-author records are tentative; the other-author one is no proof.");
        Assert.IsTrue(report.Proposals.All(proposal => proposal.Reason == "NoSharedIsbn"));
        Assert.AreEqual(1, report.Providers.Count(step => step.Status == ProviderRefreshStatus.NoProof));
        var afterRows = await rig.CatalogRowsAsync();
        Assert.AreEqual(string.Join(" || ", before), string.Join(" || ", afterRows), "A proposal is neither persisted nor a permanent match.");
    }

    [TestMethod]
    public async Task APartialRefreshKeepsWhatCommittedAndReportsTheDeadline()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true, refreshDeadline: TimeSpan.FromMilliseconds(600));
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        rig.Demo.Records["r1"] = Base(description: "Fresh words.");
        rig.OpenLibrary.Delay = TimeSpan.FromSeconds(20);

        var started = DateTimeOffset.UtcNow;
        var report = await rig.RefreshAsync(workId);

        Assert.IsLessThan(10, (DateTimeOffset.UtcNow - started).TotalSeconds, "The deadline cuts the stalled provider off cooperatively.");
        Assert.AreEqual(WorkRefreshStatus.Partial, report.Status);
        Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(report, "demo").Status);
        Assert.AreEqual(ProviderRefreshStatus.DeadlineReached, Step(report, "openlibrary", "Discover").Status);
        Assert.AreEqual("Fresh words.", (await rig.LoadWorkAsync(workId)).Description, "The earlier provider's observation committed and is kept.");
    }

    // ------------------------------------------------------------------------------------------------------------ selection (R3)

    [TestMethod]
    public async Task ContradictorySeriesPositionsEndInTheSameStateInEitherArrivalOrderAndBothClaimsSurvive()
    {
        async Task<(string? Label, decimal? Sort, SeriesPositionState? State, string[] Claims)> RunAsync(bool demoFirst)
        {
            await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
            var demo = Book("demo", "d", "Order Book", ["Order Writer"], [Edition("Order Book", Isbn1)], [Series("Order Cycle", "1")]);
            var other = Book("openlibrary", "o", "Order Book", ["Order Writer"], [Edition("Order Book", Isbn1)], [Series("Order Cycle", "2")]);
            rig.Demo.Records["d"] = demo;
            rig.OpenLibrary.Records["o"] = other;
            Guid workId;
            if (demoFirst)
            {
                workId = (await rig.ResolveAsync("demo", "d")).Work.Id;
                await rig.ResolveAsync("openlibrary", "o");
            }
            else
            {
                workId = (await rig.ResolveAsync("openlibrary", "o")).Work.Id;
                await rig.ResolveAsync("demo", "d");
            }

            var entry = (await rig.LoadWorkAsync(workId)).SeriesEntries.Single();
            var claims = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.SeriesPosition)
                .Select(claim => $"{claim.ProviderId}|{claim.IsCurrent}|{claim.ValueJson}").Order(StringComparer.Ordinal).ToArray();
            return (entry.PositionLabel, entry.PositionSort, entry.PositionState, claims);
        }

        var forwards = await RunAsync(demoFirst: true);
        var backwards = await RunAsync(demoFirst: false);

        Assert.AreEqual(SeriesPositionState.Conflicted, forwards.State);
        Assert.AreEqual(forwards.State, backwards.State);
        Assert.AreEqual(forwards.Label, backwards.Label);
        Assert.AreEqual(forwards.Sort, backwards.Sort);
        Assert.AreEqual(1m, forwards.Sort, "Equal support falls to the lowest number.");
        Assert.HasCount(2, forwards.Claims);
        CollectionAssert.AreEqual(forwards.Claims, backwards.Claims, "Both claims are retained, identically, whichever arrived first.");
    }

    [TestMethod]
    public async Task AnOriginalDateSupersedesAnEditionDerivedFallbackAndALegacyDateIsNeverOverwrittenByOne()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        // demo: a date of unknown meaning; openlibrary later supplies the original year.
        rig.Demo.Records["d"] = Book("demo", "d", "Date Book", ["Date Writer"], [Edition("Date Book", Isbn1)], date: new DateOnly(2012, 9, 18), precision: null, basis: DateBasis.EditionDerived);
        var workId = (await rig.ResolveAsync("demo", "d")).Work.Id;
        var fallback = await rig.LoadWorkAsync(workId);
        Assert.AreEqual(new DateOnly(2012, 9, 18), fallback.FirstPublicationDate, "A fallback is used while it is all there is.");
        Assert.AreEqual(DateBasis.EditionDerived, fallback.FirstPublicationBasis);

        rig.OpenLibrary.Records["o"] = Book("openlibrary", "o", "Date Book", ["Date Writer"], [Edition("Date Book", Isbn1)], date: new DateOnly(1937, 1, 1), precision: DatePrecision.Year, basis: DateBasis.Original);
        await rig.ResolveAsync("openlibrary", "o");

        var work = await rig.LoadWorkAsync(workId);
        Assert.AreEqual(new DateOnly(1937, 1, 1), work.FirstPublicationDate);
        Assert.AreEqual(DateBasis.Original, work.FirstPublicationBasis);
        Assert.AreEqual(2, (await rig.ClaimsAsync(workId)).Count(claim => claim.Field == MetadataClaimField.OriginalPublicationDate), "The fallback stays as non-selected history.");

        // A legacy Work's stored date is an unknown-basis value: an edition-derived fallback never overwrites it, but it is retained.
        var legacy = await rig.DbAsync(async database =>
        {
            var w = new Work("Legacy Dated", null, null, new DateOnly(1930, 1, 1), PublicationStatus.Published, ObservationRig.Start.AddDays(-900));
            w.AddEdition(new Edition(w.Id, "Legacy Dated", EditionFormat.Ebook, Isbn2, null, ObservationRig.Start.AddDays(-900)));
            database.Works.Add(w);
            await Task.CompletedTask;
            return w.Id;
        });
        rig.Demo.Records["l"] = Book("demo", "l", "Legacy Dated", ["Anyone"], [Edition("Legacy Dated", Isbn2)], date: new DateOnly(2020, 5, 5), basis: DateBasis.EditionDerived);
        await rig.ResolveAsync("demo", "l");
        var after = await rig.LoadWorkAsync(legacy);
        Assert.AreEqual(new DateOnly(1930, 1, 1), after.FirstPublicationDate);
        var claims = (await rig.ClaimsAsync(legacy)).Where(claim => claim.Field == MetadataClaimField.OriginalPublicationDate).ToArray();
        Assert.HasCount(2, claims);
        Assert.AreEqual("legacy-unattributed", claims.Single(claim => claim.IsSelected).ProviderId, "The legacy value participates honestly as an unattributed claim.");
        Assert.AreEqual(ObservationRig.Start.AddDays(-900), claims.Single(claim => claim.IsSelected).FirstSeenAtUtc, "Its first-seen is the Work's own created time, a real fact.");
    }

    // ------------------------------------------------------------------------------------------------------------ retention

    [TestMethod]
    public async Task RetentionKeepsFirstAndLatestSnapshotsAndTheSelectedClaimsEvidenceAcrossManyObservations()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base(description: "Description 0");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var firstObservedAt = (await rig.ObservationsAsync(workId)).Single().ObservedAtUtc;

        for (var change = 1; change <= 12; change++)
        {
            rig.Clock.Advance(TimeSpan.FromHours(1));
            rig.Demo.Records["r1"] = Base(description: $"Description {change}");
            Assert.AreEqual(ProviderRefreshStatus.Refreshed, Step(await rig.RefreshAsync(workId), "demo").Status);
        }

        var observations = await rig.ObservationsAsync(workId);
        Assert.IsLessThanOrEqualTo(4, observations.Count, "First plus the latest three, never more.");
        Assert.AreEqual(firstObservedAt, observations.Min(observation => observation.ObservedAtUtc), "The first-ever snapshot is retained.");
        var claims = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.IsLessThanOrEqualTo(11, claims.Length, "At most eleven rows per source key.");
        var selected = claims.Single(claim => claim.IsSelected);
        Assert.IsTrue(selected.IsCurrent);
        Assert.AreEqual("Description 12", (await rig.LoadWorkAsync(workId)).Description);
        Assert.IsTrue(selected.ValueJson.Contains("Description 12", StringComparison.Ordinal));
        Assert.IsTrue(claims.Any(claim => claim.ValueJson.Contains("Description 0", StringComparison.Ordinal)), "The first-ever statement is kept.");
        Assert.AreEqual(1, claims.Count(claim => claim.IsCurrent));
    }

    [TestMethod]
    public async Task ASourceFlippingBetweenTwoValuesRevisitsItsExistingRowsInsteadOfGrowing()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base(description: "Value A");
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;

        foreach (var value in new[] { "Value B", "Value A", "Value B", "Value A", "Value B" })
        {
            rig.Demo.Records["r1"] = Base(description: value);
            await rig.RefreshAsync(workId);
        }

        var claims = (await rig.ClaimsAsync(workId)).Where(claim => claim.Field == MetadataClaimField.Description).ToArray();
        Assert.HasCount(2, claims, "A, B, A, B revisits the matching value row; it does not insert a new one each time.");
        Assert.IsTrue(claims.Single(claim => claim.IsCurrent).ValueJson.Contains("Value B", StringComparison.Ordinal));
        Assert.AreEqual("Value B", (await rig.LoadWorkAsync(workId)).Description);
    }

    // ------------------------------------------------------------------------------------------------------------ retirement keeps evidence

    [TestMethod]
    public async Task RetirementKeepsTheWorksEvidenceAndAReusedReferenceStartsAFreshWorkWithItsOwn()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r1"] = Base();
        var first = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var claimsBefore = await rig.ClaimsAsync(first);
        var snapshotsBefore = await rig.ObservationsAsync(first);
        await rig.DbAsync(async database =>
        {
            var user = await database.Users.Select(item => item.Id).FirstAsync();
            var request = new BookRequest(user, first, [RequestMediaType.Ebook], null, ObservationRig.Start);
            request.Withdraw(user, ObservationRig.Start);
            database.BookRequests.Add(request);
            return 0;
        });

        await using (var scope = rig.Host.Services.CreateAsyncScope())
        {
            Assert.AreEqual(1, await scope.ServiceProvider.GetRequiredService<OrphanedWorkRetirement>().RetireAsync(rig.Clock.UtcNow, CancellationToken.None));
        }

        Assert.AreEqual(0, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference => reference.EntityId == first)), "Unique references are released.");
        Assert.AreEqual(0, await rig.DbAsync(database => database.Editions.CountAsync(edition => edition.WorkId == first)));
        CollectionAssert.AreEqual(claimsBefore.Select(claim => claim.Id).ToArray(), (await rig.ClaimsAsync(first)).Select(claim => claim.Id).ToArray(), "Claims stay on their original Work.");
        CollectionAssert.AreEqual(snapshotsBefore.Select(item => item.Id).ToArray(), (await rig.ObservationsAsync(first)).Select(item => item.Id).ToArray());

        var second = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        Assert.AreNotEqual(first, second);
        var secondClaims = await rig.ClaimsAsync(second);
        Assert.IsNotEmpty(secondClaims);
        Assert.IsTrue(secondClaims.All(claim => claim.WorkId == second), "The new Work does not inherit the retired Work's evidence.");
        Assert.IsFalse(secondClaims.Select(claim => claim.Id).Intersect(claimsBefore.Select(claim => claim.Id)).Any());
    }

    // ------------------------------------------------------------------------------------------------------------ notifications

    [TestMethod]
    public async Task RefreshDiscoveryAttachAndCorrectionsNeverNotifyOrResetAReadReceipt()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["r1"] = Base();
        var workId = (await rig.ResolveAsync("demo", "r1")).Work.Id;
        var readerId = await rig.DbAsync(async database => await database.Users.Select(user => user.Id).FirstAsync());
        var authorId = (await rig.LoadWorkAsync(workId)).Authors.Single().AuthorId;
        await rig.DbAsync(async database =>
        {
            database.Follows.Add(new FamilyLibrarian.Domain.Following.Follow(
                readerId, FamilyLibrarian.Domain.Following.FollowSubjectType.Author, authorId, ObservationRig.Start));
            // A notification the reader has already read: its receipt must survive everything below.
            var read = new FamilyLibrarian.Domain.Notifications.NotificationEvent(
                FamilyLibrarian.Domain.Notifications.NotificationAudience.SingleUser, readerId,
                FamilyLibrarian.Domain.Notifications.NotificationCategories.AuthorNewWorkDetected,
                FamilyLibrarian.Domain.Notifications.NotificationSeverity.Info, "Added to the catalog", null, "Work", workId.ToString(), ObservationRig.Start);
            database.NotificationEvents.Add(read);
            var receipt = new FamilyLibrarian.Domain.Notifications.NotificationReceipt(read.Id, readerId);
            receipt.MarkRead(ObservationRig.Start.AddHours(1));
            database.NotificationReceipts.Add(receipt);
            await Task.CompletedTask;
            return 0;
        });
        var notifications = await NotificationRowsAsync(rig);

        // A correction, a new edition, a contradicting record, a proof-bearing second source and a tentative proposal.
        rig.Demo.Records["r1"] = Base(description: "Corrected.") with { Editions = [Edition("Refresh Book", Isbn1, "Ebook", "en"), Edition("Refresh Book", Isbn2, "Paperback", "en")] };
        await rig.RefreshAsync(workId);
        rig.OpenLibrary.SearchResults.Add(Book("openlibrary", "ol", "Refresh Book", ["Rae Writer"], [Edition("Refresh Book", Isbn1, "Ebook", "en")], language: "en"));
        rig.OpenLibrary.Records["ol"] = rig.OpenLibrary.SearchResults[0];
        await rig.RefreshAsync(workId);
        rig.Demo.Records["r1"] = Base() with { Language = "de", Editions = [Edition("Refresh Book", Isbn3, "Ebook", "de")] };
        await rig.RefreshAsync(workId);
        await rig.RefreshAsync(workId);

        CollectionAssert.AreEqual(notifications, await NotificationRowsAsync(rig), "Every notification event and read receipt, value for value, is untouched.");
    }

    private static readonly string[] NotificationTables = ["notification_events", "notification_receipts"];

    private static Task<string[]> NotificationRowsAsync(ObservationRig rig) =>
        rig.DbAsync(async database =>
        {
            var rows = new List<string>();
            await using var data = new Npgsql.NpgsqlConnection(rig.Connection);
            await data.OpenAsync();
            foreach (var table in NotificationTables)
            {
                var name = await TableNameAsync(data, table);
                await using var command = new Npgsql.NpgsqlCommand($"SELECT to_jsonb(t.*)::text || t.xmin::text FROM {name} t", data);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(table + "|" + reader.GetString(0));
            }

            rows.Sort(StringComparer.Ordinal);
            await Task.CompletedTask;
            _ = database;
            return rows.ToArray();
        });

    private static async Task<string> TableNameAsync(Npgsql.NpgsqlConnection data, string table)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT quote_ident(schemaname) || '.' || quote_ident(tablename) FROM pg_tables WHERE tablename = @name", data);
        command.Parameters.AddWithValue("name", table);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    // ------------------------------------------------------------------------------------------------------------ AF-10 input

    [TestMethod]
    public async Task ABetterDateInputChangesTheAf10ScheduleWhileItsFormulaStaysTheSame()
    {
        await using var rig = await ObservationRig.CreateAsync();
        // The record states only a future year. It used to map to no date at all, so a request anchored on the request day.
        rig.Demo.Records["f"] = Book("demo", "f", "Forthcoming Book", ["Future Writer"], [Edition("Forthcoming Book", Isbn1)],
            date: new DateOnly(2027, 1, 1), precision: DatePrecision.Year, basis: DateBasis.Original);
        var workId = (await rig.ResolveAsync("demo", "f")).Work.Id;

        await using var scope = rig.Host.Services.CreateAsyncScope();
        var lookup = await scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Publishing.IWorkLookup>().FindAsync(workId, CancellationToken.None);
        var now = rig.Clock.UtcNow;

        Assert.AreEqual(new DateOnly(2027, 1, 1), lookup!.FirstPublicationDate);
        Assert.AreEqual(TimeSpan.FromDays(7), FamilyLibrarian.Application.Acquisition.RecheckSchedule.WaitingInterval(lookup.FirstPublicationDate, now, now),
            "A known future release date now paces the look at the clamped pre-release weekly interval.");
        Assert.AreEqual(TimeSpan.FromDays(1), FamilyLibrarian.Application.Acquisition.RecheckSchedule.WaitingInterval(null, now, now),
            "The unchanged formula given no date: the earlier behavior for the same book.");
        Assert.AreEqual(PublicationStatus.Forthcoming, (await rig.LoadWorkAsync(workId)).PublicationStatus);
    }
}
