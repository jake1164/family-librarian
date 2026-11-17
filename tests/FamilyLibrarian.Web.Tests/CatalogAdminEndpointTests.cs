using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FamilyLibrarian.Web.Tests.Harness.RigBooks;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The administrator refresh route and the read-only inspection/report routes: host-side authorization and anti-forgery, validation,
/// bounds, honest provenance (legacy references and removed providers), no secrets and, above all, no writes.
/// </summary>
[TestClass]
public sealed class CatalogAdminEndpointTests
{
    private const string Isbn1 = "9783000000001";

    private static readonly string[] TwinNameOnly = ["Twin Name"];

    private static async Task<HttpClient> ClientAsync(ObservationRig rig, string? email, string? password, bool antiforgery = true)
    {
        var client = rig.Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (email is not null)
        {
            using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password! });
            Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
            if (antiforgery)
            {
                client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
            }
        }

        return client;
    }

    private static Task<HttpClient> AdminAsync(ObservationRig rig, bool antiforgery = true) =>
        ClientAsync(rig, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword, antiforgery);

    private static Task<HttpClient> ReaderAsync(ObservationRig rig) =>
        ClientAsync(rig, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

    // ------------------------------------------------------------------------------------------------------------ authorization

    [TestMethod]
    public async Task EveryAdminCatalogRouteDeniesAnonymousAndOrdinaryUsersAndRefreshNeedsAntiForgery()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r"] = Book("demo", "r", "Guarded Book", ["Guard Writer"], [Edition("Guarded Book", Isbn1)]);
        var workId = (await rig.ResolveAsync("demo", "r")).Work.Id;
        var routes = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Post, $"/api/v1/admin/catalog/works/{workId}/refresh"),
            (HttpMethod.Get, $"/api/v1/admin/catalog/works/{workId}/sources"),
            (HttpMethod.Get, "/api/v1/admin/catalog/issues?category=possible-duplicates")
        };
        var before = await rig.CatalogRowsAsync();
        var calls = rig.Demo.DetailCalls;

        using var anonymous = await ClientAsync(rig, null, null);
        using var reader = await ReaderAsync(rig);
        foreach (var (method, url) in routes)
        {
            using (var denied = await anonymous.SendAsync(new HttpRequestMessage(method, url)))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode, $"anonymous {method} {url}");
            }

            using var forbidden = await reader.SendAsync(new HttpRequestMessage(method, url));
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode, $"ordinary user {method} {url}");
        }

        // An admin session without the anti-forgery header cannot change anything.
        using var noToken = await AdminAsync(rig, antiforgery: false);
        using var rejected = await noToken.PostAsync($"/api/v1/admin/catalog/works/{workId}/refresh", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);

        Assert.AreEqual(calls, rig.Demo.DetailCalls, "No denied call reached a provider.");
        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync(), "No denied call wrote anything.");
    }

    // ------------------------------------------------------------------------------------------------------------ refresh

    [TestMethod]
    public async Task AnAdministratorRefreshReportsEachStepAndNeverChangesRequestsOwnershipOrReferences()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["r"] = Book("demo", "r", "Refreshed Book", ["Re Writer"], [Edition("Refreshed Book", Isbn1)], description: "Old.");
        var workId = (await rig.ResolveAsync("demo", "r")).Work.Id;
        rig.Demo.Records["r"] = rig.Demo.Records["r"] with { Description = "New." };
        using var admin = await AdminAsync(rig);

        using var response = await admin.PostAsync($"/api/v1/admin/catalog/works/{workId}/refresh", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var report = (await response.Content.ReadFromJsonAsync<WorkRefreshResponse>())!;
        Assert.AreEqual("Completed", report.Status);
        Assert.AreEqual("Refreshed", report.Steps.Single(step => step.ProviderId == "demo").Status);
        Assert.AreEqual("Refresh", report.Steps.Single(step => step.ProviderId == "demo").Phase);
        Assert.AreEqual(1, await rig.DbAsync(database => database.ExternalReferences.CountAsync(reference => reference.EntityId == workId)));

        using var unknown = await admin.PostAsync($"/api/v1/admin/catalog/works/{Guid.NewGuid()}/refresh", null);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        await rig.DbAsync(async database =>
        {
            await database.Database.ExecuteSqlRawAsync("UPDATE catalog.works SET is_retired = true WHERE id = {0}", workId);
            return 0;
        });
        using var retired = await admin.PostAsync($"/api/v1/admin/catalog/works/{workId}/refresh", null);
        Assert.AreEqual(HttpStatusCode.Conflict, retired.StatusCode);
        using var problem = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
        Assert.AreEqual("catalog.work_retired", problem.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------------------------------------------ sources

    [TestMethod]
    public async Task TheSourcesViewShowsProvenanceSelectedVersusClaimedAndNeverInventsEvidence()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        rig.Demo.Records["d"] = Book("demo", "d", "Shown Book", ["Show Writer"], [Edition("Shown Book", Isbn1)], [Series("Show Cycle", "1")], "Demo words.");
        var workId = (await rig.ResolveAsync("demo", "d")).Work.Id;
        rig.OpenLibrary.Records["o"] = Book("openlibrary", "o", "Shown Book", ["Show Writer"], [Edition("Shown Book", Isbn1)], [Series("Show Cycle", "2")], "Open words.");
        await rig.ResolveAsync("openlibrary", "o");

        // A legacy reference (no snapshot), and one whose provider is no longer installed at all.
        await rig.DbAsync(async database =>
        {
            database.ExternalReferences.Add(new ExternalReference("demo", ExternalReferenceEntityType.Work, workId, "legacy-ref", ObservationRig.Start.AddDays(-400)));
            database.ExternalReferences.Add(new ExternalReference("removed-source", ExternalReferenceEntityType.Work, workId, "gone-1", ObservationRig.Start.AddDays(-300)));
            await Task.CompletedTask;
            return 0;
        });
        var before = await rig.CatalogRowsAsync();
        using var admin = await AdminAsync(rig);

        var view = (await admin.GetFromJsonAsync<WorkSourcesAdminResponse>($"/api/v1/admin/catalog/works/{workId}/sources"))!;

        Assert.AreEqual("Shown Book", view.Title);
        var legacy = view.References.Single(reference => reference.ExternalId == "legacy-ref");
        Assert.AreEqual("NoSnapshotRetained", legacy.Provenance, "A reference with no snapshot says so rather than inventing provenance.");
        Assert.IsNull(legacy.LastSeenAtUtc);
        var removed = view.References.Single(reference => reference.ExternalId == "gone-1");
        Assert.AreEqual("NotConfigured", removed.ProviderState);
        Assert.IsNull(removed.ProviderName);
        var live = view.References.Single(reference => reference.ExternalId == "d");
        Assert.AreEqual("Snapshot", live.Provenance);
        Assert.AreEqual("Configured", live.ProviderState);
        Assert.AreEqual("Rig demo", live.ProviderName);
        Assert.HasCount(2, view.Observations);
        Assert.IsTrue(view.Observations.All(observation => observation.Outcome == "Observed" && observation.Snapshot is not null));

        var series = view.Facts.Single(fact => fact.Field == "SeriesPosition");
        Assert.AreEqual("Conflicted", series.State);
        Assert.HasCount(2, series.Claims);
        Assert.IsTrue(series.Claims.Single(claim => !claim.IsSelected).ContradictsSelected, "The dissent is flagged against the selected value.");
        var description = view.Facts.Single(fact => fact.Field == "Description");
        Assert.AreEqual("Demo words.", description.SelectedValue!.Value.GetProperty("text").GetString());
        Assert.IsTrue(description.Claims.Single(claim => claim.ProviderId == "openlibrary").ContradictsSelected);

        // No secrets and no raw payload anywhere in what was sent.
        var text = await admin.GetStringAsync($"/api/v1/admin/catalog/works/{workId}/sources");
        foreach (var forbidden in new[] { "Password", "ConnectionString", "apiKey", "ApiKey", "token", "secret", "Host=" })
        {
            Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"The inspection response must not contain '{forbidden}'.");
        }

        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync(), "Inspection writes nothing: every row value is identical.");
        Assert.AreEqual(0, rig.Demo.DetailCalls - 1 + rig.OpenLibrary.DetailCalls - 1, "Inspection never calls a provider.");

        using var missing = await admin.GetAsync($"/api/v1/admin/catalog/works/{Guid.NewGuid()}/sources");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task TheSourcesViewIsBoundedWhateverAWorkHasAccumulated()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["b"] = Book("demo", "b", "Busy Book", ["Busy Writer"], [Edition("Busy Book", Isbn1)]);
        var workId = (await rig.ResolveAsync("demo", "b")).Work.Id;
        var big = "{\"filler\":\"" + new string('x', 20_000) + "\"}";
        await rig.DbAsync(async database =>
        {
            for (var index = 0; index < 60; index++)
            {
                database.SourceObservations.Add(new SourceObservation(
                    workId, "demo", $"bulk-{index}", index.ToString("D64", System.Globalization.CultureInfo.InvariantCulture), index == 0 ? big : "{\"outcome\":\"Observed\"}", 1,
                    ObservationRig.Start.AddMinutes(index)));
            }

            for (var index = 0; index < 150; index++)
            {
                database.MetadataClaims.Add(new MetadataClaim(
                    workId, MetadataClaimField.Description, $"scope-{index:D3}", "demo", "bulk", "{\"text\":\"value\"}", index.ToString("D64", System.Globalization.CultureInfo.InvariantCulture), ObservationRig.Start));
            }

            await Task.CompletedTask;
            return 0;
        });
        using var admin = await AdminAsync(rig);

        var view = (await admin.GetFromJsonAsync<WorkSourcesAdminResponse>($"/api/v1/admin/catalog/works/{workId}/sources"))!;

        Assert.HasCount(50, view.Observations);
        Assert.IsTrue(view.ObservationsTruncated, "Truncation is disclosed, never silent.");
        Assert.IsLessThanOrEqualTo(100, view.Facts.Sum(fact => fact.Claims.Count));
        Assert.IsTrue(view.ClaimsTruncated);
        Assert.IsTrue(view.Observations.All(observation => observation.Snapshot is null || observation.Snapshot.Value.GetRawText().Length <= 8 * 1024),
            "No rendered snapshot exceeds 8 KB.");
        var oversize = view.Observations.SingleOrDefault(observation => observation.ExternalId == "bulk-0");
        if (oversize is not null)
        {
            Assert.IsTrue(oversize.SnapshotTruncated);
            Assert.IsNull(oversize.Snapshot);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ report

    [TestMethod]
    public async Task TheIssueReportListsEveryCategoryInBoundedPagesAndWritesNothing()
    {
        await using var rig = await ObservationRig.CreateAsync(enableOpenLibrary: true);
        var seeded = await rig.DbAsync(async database =>
        {
            // Two Works sharing a title and first author; two authors and two series sharing names.
            var author = new Author("Dup Author", null, ObservationRig.Start);
            var namesake = new Author("Twin Name", null, ObservationRig.Start);
            var twin = new Author("Twin Name", null, ObservationRig.Start.AddDays(1));
            var series = new Series("Twin Series", SeriesStatus.Unknown, ObservationRig.Start);
            var secondSeries = new Series("Twin Series", SeriesStatus.Unknown, ObservationRig.Start.AddDays(1));
            database.Authors.AddRange(author, namesake, twin);
            database.Series.AddRange(series, secondSeries);
            for (var index = 0; index < 2; index++)
            {
                var work = new Work("Duplicate Title", null, null, null, PublicationStatus.Unknown, ObservationRig.Start.AddDays(index));
                work.AddAuthor(author, 0);
                database.Works.Add(work);
            }

            await Task.CompletedTask;
            return 0;
        });
        _ = seeded;
        // A provisional credit and an unbound claim (ambiguous name and series), and a contested observation (a contradicted refresh).
        rig.Demo.Records["a"] = Book("demo", "a", "Ambiguous Book", ["Twin Name"], [Edition("Ambiguous Book", Isbn1)], [Series("Twin Series", "1")]);
        await rig.ResolveAsync("demo", "a");
        rig.Demo.Records["a"] = rig.Demo.Records["a"] with { Authors = ["Wholly Different Person"] };
        await rig.RefreshAsync((await rig.DbAsync(database => database.ExternalReferences.SingleAsync(reference => reference.ExternalId == "a"))).EntityId);
        var before = await rig.CatalogRowsAsync();
        using var admin = await AdminAsync(rig);

        async Task<CatalogIssueReportResponse> PageAsync(string category, string? cursor = null, int take = 50) =>
            (await admin.GetFromJsonAsync<CatalogIssueReportResponse>(
                $"/api/v1/admin/catalog/issues?category={category}&take={take}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")))!;

        Assert.AreEqual("PossibleDuplicate", (await PageAsync("possible-duplicates")).Items.Single().Kind);
        Assert.AreEqual(2, (await PageAsync("possible-duplicates")).Items.Single().Ids.Count);
        Assert.AreEqual("SameNameAuthor", (await PageAsync("same-name-authors")).Items.Single().Kind);
        Assert.AreEqual("SameNameSeries", (await PageAsync("same-name-series")).Items.Single().Kind);
        Assert.AreEqual("ProvisionalCredit", (await PageAsync("provisional-credits")).Items.Single().Kind);
        Assert.AreEqual("UnboundSeriesClaim", (await PageAsync("unbound-series-claims")).Items.Single().Kind);
        Assert.AreEqual("Contradiction", (await PageAsync("contested-observations")).Items.Single().Kind);
        Assert.IsEmpty((await PageAsync("conflicted-positions")).Items);

        // Paging by one walks every item exactly once and ends.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync("same-name-authors", cursor, take: 1);
            seen.AddRange(page.Items.Select(item => item.Subject));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);
        CollectionAssert.AreEqual(TwinNameOnly, seen);

        CollectionAssert.AreEqual(before, await rig.CatalogRowsAsync(), "The report writes nothing.");
    }

    [TestMethod]
    public async Task TheIssueReportValidatesItsInputAndPagesDuplicateScansWithoutLoadingTheCatalog()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await rig.DbAsync(async database =>
        {
            var author = new Author("Many Author", null, ObservationRig.Start);
            database.Authors.Add(author);
            for (var group = 0; group < 7; group++)
            {
                for (var copy = 0; copy < 2; copy++)
                {
                    var work = new Work($"Title {group:D2}", null, null, null, PublicationStatus.Unknown, ObservationRig.Start.AddMinutes(copy));
                    work.AddAuthor(author, 0);
                    database.Works.Add(work);
                }
            }

            await Task.CompletedTask;
            return 0;
        });
        using var admin = await AdminAsync(rig);

        foreach (var bad in new[]
                 {
                     "/api/v1/admin/catalog/issues", "/api/v1/admin/catalog/issues?category=nope",
                     "/api/v1/admin/catalog/issues?category=possible-duplicates&take=0",
                     "/api/v1/admin/catalog/issues?category=possible-duplicates&take=101",
                     "/api/v1/admin/catalog/issues?category=possible-duplicates&cursor=%25%25%25",
                     "/api/v1/admin/catalog/issues?category=possible-duplicates&cursor=" + new string('A', 600)
                 })
        {
            using var response = await admin.GetAsync(bad);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, bad);
        }

        var titles = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/v1/admin/catalog/issues?category=possible-duplicates&take=3" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await admin.GetFromJsonAsync<CatalogIssueReportResponse>(url))!;
            Assert.IsLessThanOrEqualTo(3, page.Items.Count);
            titles.AddRange(page.Items.Select(item => item.Subject));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 20);

        CollectionAssert.AreEqual(Enumerable.Range(0, 7).Select(group => $"Title {group:D2}").ToArray(), titles, "Every duplicate group appears exactly once across pages.");
        Assert.IsGreaterThanOrEqualTo(3, pages);
    }

    // ------------------------------------------------------------------------------------------------------------ V5: continuation and cursors

    private static async Task<(List<CatalogIssueResponse> Items, int Pages)> WalkAsync(HttpClient admin, string category, int take, int pageLimit = 20)
    {
        var items = new List<CatalogIssueResponse>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/v1/admin/catalog/issues?category={category}&take={take}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await admin.GetFromJsonAsync<CatalogIssueReportResponse>(url))!;
            Assert.IsLessThanOrEqualTo(take, page.Items.Count);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < pageLimit);
        Assert.IsNull(cursor, "The walk ended instead of looping.");
        return (items, pages);
    }

    private static readonly string[] ExpectedDuplicateSubjects = ["Alpha Book", "Shared Title", "Shared Title", "Zulu Book"];

    private static Task<int> SeedDuplicateWorksAsync(ObservationRig rig, params (string Title, string Author, int Count)[] groups) =>
        rig.DbAsync(async database =>
        {
            var authors = new Dictionary<string, Author>(StringComparer.Ordinal);
            var created = ObservationRig.Start;
            foreach (var (title, name, count) in groups)
            {
                if (!authors.TryGetValue(name, out var author))
                {
                    author = new Author(name, null, created);
                    authors[name] = author;
                    database.Authors.Add(author);
                }

                for (var copy = 0; copy < count; copy++)
                {
                    var work = new Work(title, null, null, null, PublicationStatus.Unknown, created.AddSeconds(copy));
                    work.AddAuthor(author, 0);
                    database.Works.Add(work);
                }
            }

            await Task.CompletedTask;
            return 0;
        });

    [TestMethod]
    public async Task TheDuplicateReportAdvancesBetweenTwoAuthorGroupsThatShareOneTitle()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await SeedDuplicateWorksAsync(rig, ("Shared Title", "First Author", 2), ("Shared Title", "Second Author", 2));
        using var admin = await AdminAsync(rig);

        var (items, pages) = await WalkAsync(admin, "possible-duplicates", take: 1);

        Assert.HasCount(2, items, "Each (title, first author) group appears exactly once.");
        Assert.AreEqual(4, items.SelectMany(item => item.Ids).Distinct().Count(), "The two pages name different Works.");
        Assert.AreEqual(2, pages, "The second page is the last: there is nothing after the last group.");
    }

    [TestMethod]
    public async Task TheDuplicateReportReportsEveryGroupOnceWhenEqualTitleGroupsCrossAndExceedTheScanWindow()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await SeedDuplicateWorksAsync(
            rig,
            ("Alpha Book", "Pat Author", 2),
            ("Shared Title", "Ann Author", 1_300),
            ("Shared Title", "Bob Author", 1_300),
            ("Zulu Book", "Pat Author", 2));
        using var admin = await AdminAsync(rig);

        foreach (var take in new[] { 1, 3, 100 })
        {
            var (items, _) = await WalkAsync(admin, "possible-duplicates", take);

            Assert.HasCount(4, items, $"take={take}: no group skipped or repeated, however large.");
            CollectionAssert.AreEqual(ExpectedDuplicateSubjects, items.Select(item => item.Subject).ToArray());
            Assert.AreEqual(4, items.Select(item => item.Ids[0]).Distinct().Count());
        }

        // A page size that changes between requests continues from the same position.
        var first = (await admin.GetFromJsonAsync<CatalogIssueReportResponse>("/api/v1/admin/catalog/issues?category=possible-duplicates&take=1"))!;
        var continued = new List<CatalogIssueResponse>(first.Items);
        var cursor = first.NextCursor;
        var pages = 1;
        while (cursor is not null && pages++ < 10)
        {
            var next = (await admin.GetFromJsonAsync<CatalogIssueReportResponse>(
                $"/api/v1/admin/catalog/issues?category=possible-duplicates&take=100&cursor={Uri.EscapeDataString(cursor)}"))!;
            continued.AddRange(next.Items);
            cursor = next.NextCursor;
        }

        Assert.IsNull(cursor);
        CollectionAssert.AreEqual(ExpectedDuplicateSubjects, continued.Select(item => item.Subject).ToArray(), "Changing the page size mid-walk neither repeats nor skips.");
    }

    [TestMethod]
    [DataRow("possible-duplicates")]
    [DataRow("same-name-authors")]
    [DataRow("same-name-series")]
    [DataRow("provisional-credits")]
    [DataRow("unbound-series-claims")]
    [DataRow("conflicted-positions")]
    [DataRow("contested-observations")]
    public async Task ACursorThatDecodesButIsNotAKeyOfThatCategoryIsRejectedNotTreatedAsTheFirstPage(string category)
    {
        await using var rig = await ObservationRig.CreateAsync();
        using var admin = await AdminAsync(rig);

        foreach (var cursor in new[] { "YWJj", "AAAA", "djF8bm9uZQ" })
        {
            using var response = await admin.GetAsync($"/api/v1/admin/catalog/issues?category={category}&cursor={cursor}");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"{category} with cursor {cursor}");
        }
    }

    [TestMethod]
    public async Task AValidCursorOfOneCategoryIsRejectedByAnother()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await rig.DbAsync(async database =>
        {
            database.Authors.AddRange(
                new Author("Twin One", null, ObservationRig.Start), new Author("Twin One", null, ObservationRig.Start.AddDays(1)),
                new Author("Twin Two", null, ObservationRig.Start), new Author("Twin Two", null, ObservationRig.Start.AddDays(1)));
            await Task.CompletedTask;
            return 0;
        });
        using var admin = await AdminAsync(rig);
        var page = (await admin.GetFromJsonAsync<CatalogIssueReportResponse>("/api/v1/admin/catalog/issues?category=same-name-authors&take=1"))!;
        Assert.IsNotNull(page.NextCursor);

        foreach (var other in new[] { "possible-duplicates", "same-name-series", "provisional-credits", "unbound-series-claims" })
        {
            using var response = await admin.GetAsync($"/api/v1/admin/catalog/issues?category={other}&cursor={Uri.EscapeDataString(page.NextCursor!)}");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, other);
        }

        using var same = await admin.GetAsync($"/api/v1/admin/catalog/issues?category=same-name-authors&cursor={Uri.EscapeDataString(page.NextCursor!)}");
        Assert.AreEqual(HttpStatusCode.OK, same.StatusCode);
    }

    [TestMethod]
    public async Task TheReaderItselfRejectsAMalformedDecodedCursorInsteadOfRestartingAtTheFirstPage()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await using var scope = rig.Host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.Catalog.Observation.ICatalogEvidenceReader>();

        foreach (var category in Enum.GetValues<FamilyLibrarian.Application.Catalog.Observation.EvidenceIssueCategory>())
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await reader.ListIssuesAsync(category, "YWJj", 10, CancellationToken.None));
        }
    }

    // ------------------------------------------------------------------------------------------------------------ W2: every legal key can continue

    private static readonly char[] NameGroupLetters = ['a', '界', 'z'];

    private static string Letters(char letter, int length) => new(letter, length);

    [TestMethod]
    [DataRow(350, 'b', 64)]
    [DataRow(600, 'b', 64)]
    [DataRow(1_000, 'b', 512)]
    [DataRow(1_000, '界', 512)]
    public async Task ADuplicateReportWalksTitlesAndAuthorsUpToTheModelLimits(int titleLength, char letter, int authorLength)
    {
        await using var rig = await ObservationRig.CreateAsync();
        var title = Letters(letter, titleLength);
        await SeedDuplicateWorksAsync(
            rig,
            ("a short title", "Pat Author", 2),
            (title, Letters(letter, authorLength - 1) + "x", 2),
            (title, Letters(letter, authorLength - 1) + "y", 2),
            (title[..^1] + "c", Letters(letter, authorLength), 2));
        using var admin = await AdminAsync(rig);

        foreach (var take in new[] { 1, 2 })
        {
            var (items, _) = await WalkAsync(admin, "possible-duplicates", take, pageLimit: 10);

            Assert.HasCount(4, items, $"take={take}: every group once, no 400 or 500 on the way.");
            Assert.AreEqual(8, items.SelectMany(item => item.Ids).Distinct().Count(), $"take={take}: every Work once.");
        }
    }

    [TestMethod]
    public async Task EverySingleTextCategoryContinuesFromItsLongestLegalName()
    {
        await using var rig = await ObservationRig.CreateAsync();
        await rig.DbAsync(async database =>
        {
            foreach (var group in NameGroupLetters)
            {
                var name = Letters(group, 511) + "q";
                database.Authors.AddRange(new Author(name, null, ObservationRig.Start), new Author(name, null, ObservationRig.Start.AddDays(1)));
                var series = Letters(group, 999) + "q";
                database.Series.AddRange(
                    new Series(series, SeriesStatus.Unknown, ObservationRig.Start), new Series(series, SeriesStatus.Unknown, ObservationRig.Start.AddDays(1)));
            }

            await Task.CompletedTask;
            return 0;
        });
        using var admin = await AdminAsync(rig);

        foreach (var category in new[] { "same-name-authors", "same-name-series" })
        {
            var (items, pages) = await WalkAsync(admin, category, take: 1, pageLimit: 10);

            Assert.HasCount(3, items, category);
            Assert.AreEqual(3, pages, category);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ V6: rendered byte budget

    [TestMethod]
    public async Task ARenderedSnapshotIsBoundedByTheEightKibibytesTheClientReceivesNotByItsStoredCharacters()
    {
        await using var rig = await ObservationRig.CreateAsync();
        rig.Demo.Records["b"] = Book("demo", "b", "Cjk Book", ["Cjk Writer"], [Edition("Cjk Book", Isbn1)]);
        var workId = (await rig.ResolveAsync("demo", "b")).Work.Id;
        await rig.DbAsync(async database =>
        {
            // 2,000 characters are 6 KB stored but 12 KB once escaped; 1,000 are 3 KB stored and 6 KB escaped.
            database.SourceObservations.Add(new SourceObservation(
                workId, "demo", "wide", new string('1', 64), "{\"outcome\":\"Observed\",\"filler\":\"" + new string('界', 2_000) + "\"}", 1, ObservationRig.Start.AddMinutes(1)));
            database.SourceObservations.Add(new SourceObservation(
                workId, "demo", "narrow", new string('2', 64), "{\"outcome\":\"Observed\",\"filler\":\"" + new string('界', 1_000) + "\"}", 1, ObservationRig.Start.AddMinutes(2)));
            await Task.CompletedTask;
            return 0;
        });
        using var admin = await AdminAsync(rig);

        var view = (await admin.GetFromJsonAsync<WorkSourcesAdminResponse>($"/api/v1/admin/catalog/works/{workId}/sources"))!;

        var wide = view.Observations.Single(observation => observation.ExternalId == "wide");
        Assert.IsTrue(wide.SnapshotTruncated, "As rendered it exceeds 8 KiB, so it is withheld and says so.");
        Assert.IsNull(wide.Snapshot);
        var narrow = view.Observations.Single(observation => observation.ExternalId == "narrow");
        Assert.IsFalse(narrow.SnapshotTruncated);
        Assert.IsLessThanOrEqualTo(8 * 1024, System.Text.Encoding.UTF8.GetByteCount(narrow.Snapshot!.Value.GetRawText()), "What the client was sent fits the budget.");
    }
}
