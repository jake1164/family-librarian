using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Notifications;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The request workflow against the real host and a real PostgreSQL database.
/// </summary>
/// <remarks>
/// Nothing is stubbed: requests are created through the same endpoints the
/// browser calls, with a genuine Identity cookie and a genuine anti-forgery
/// token, and the assertions about ownership are the ones a curious family
/// member could test by hand.
/// </remarks>
[TestClass]
public sealed class RequestWorkflowEndpointTests
{
    private static readonly string[] BothFormats = ["Ebook", "Audiobook"];
    private static readonly string[] CancelOnly = ["Cancelled"];
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static WebTestFixture? _fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AUserCanRequestBothFormatsAndSeeItInTheirOwnList()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "the-hobbit");

        var created = await CreateRequestAsync(client, workId, ["Ebook", "Audiobook"], "For the car.");

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        Assert.AreEqual("PendingAcquisition", request.Status);
        Assert.IsTrue(request.IsActive);
        Assert.AreEqual("For the car.", request.Note);
        CollectionAssert.AreEquivalent(
            BothFormats,
            request.Formats.Select(format => format.MediaType).ToArray());

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        Assert.IsTrue(mine.Active.Any(item => item.Id == request.Id));
        Assert.AreEqual("The Hobbit", mine.Active.Single(item => item.Id == request.Id).WorkTitle);
    }

    [TestMethod]
    public async Task ASecondOverlappingRequestIsAnsweredWithTheOutstandingOne()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "a-wrinkle-in-time");

        var first = await CreateRequestAsync(client, workId, ["Ebook"], null);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateRequestAsync(client, workId, ["Ebook"], null);

        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        var initial = await first.Content.ReadFromJsonAsync<BookRequestResponse>();
        var repeated = await second.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(initial);
        Assert.IsNotNull(repeated);
        Assert.AreEqual(initial.Id, repeated.Id);
        Assert.AreEqual(1, repeated.RequesterCount);

        var bareOverride = await CreateRequestAsync(client, workId, ["Ebook"], null, confirmDuplicate: true);
        Assert.AreEqual(HttpStatusCode.BadRequest, bareOverride.StatusCode);
        var variant = await client.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, true, false, "Language", "Spanish translation"));
        Assert.AreEqual(HttpStatusCode.Created, variant.StatusCode);
        var review = await variant.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(review);
        Assert.AreNotEqual(initial.Id, review.Id);
        Assert.AreEqual("NeedsReview", review.Status);
        Assert.IsTrue(review.RequiresManualFulfillment);
    }

    [TestMethod]
    public async Task ARequestForAnOwnedEbookIsAnsweredWithAnOwnedWarning()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient("42"));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(client, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        var userToken = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, userToken);

        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(adminClient, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var adminToken = await WebTestFixture.GetAntiforgeryTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, adminToken);
        await ConfigureCwaAsync(adminClient);

        var workId = await ResolveWorkAsync(client, "the-hobbit");

        var response = await CreateRequestAsync(client, workId, ["Ebook"], null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = await response.Content.ReadFromJsonAsync<CreateBookRequestConflictResponse>();
        Assert.IsNotNull(conflict);
        Assert.AreEqual("Owned", conflict.Kind);
        Assert.IsNotNull(conflict.Owned);
        var owned = conflict.Owned.OwnedFormats.Single();
        Assert.AreEqual("Ebook", owned.MediaType);
        Assert.AreEqual("cwa", owned.ProviderId);

        var confirmed = await client.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, true, "Edition", "Illustrated edition, ISBN 9780000000000"));
        Assert.AreEqual(HttpStatusCode.Created, confirmed.StatusCode);
        var review = await confirmed.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(review);
        Assert.AreEqual("NeedsReview", review.Status);
    }

    [TestMethod]
    public async Task ARequestForAnUnknownWorkIsNotFound()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);

        var response = await CreateRequestAsync(client, Guid.NewGuid(), ["Ebook"], null);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ARequestWithNoFormatIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");

        var response = await CreateRequestAsync(client, workId, [], null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ARequesterCanWithdrawAndSafelyReopenTheirRequest()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");
        var request = await CreateAndReadRequestAsync(client, workId, ["Audiobook"]);

        var cancelled = await TransitionAsync(client, request.Id, "Cancelled", "Borrowed it.");
        await AssertStatusAsync(HttpStatusCode.OK, cancelled);
        var afterCancel = await cancelled.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(afterCancel);
        Assert.AreEqual("Cancelled", afterCancel.Status);
        Assert.IsFalse(afterCancel.IsActive);
        Assert.IsTrue(afterCancel.Formats.All(format => format.Status == "Cancelled"));

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        Assert.IsTrue(mine.History.Any(item => item.Id == request.Id));
        Assert.IsFalse(mine.Active.Any(item => item.Id == request.Id));

        var reopened = await TransitionAsync(client, request.Id, "PendingAcquisition", null);
        Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
        var newRequest = await reopened.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(newRequest);
        Assert.AreEqual(request.Id, newRequest.Id);
        Assert.AreEqual("PendingAcquisition", newRequest.Status);
    }

    [TestMethod]
    public async Task ARequesterCannotDriveAStatusReservedForTheLibrarian()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Ebook"]);

        var needsReview = await TransitionAsync(client, request.Id, "NeedsReview", null);
        var notAvailable = await TransitionAsync(client, request.Id, "NotAvailable", null);

        Assert.AreEqual(HttpStatusCode.BadRequest, needsReview.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notAvailable.StatusCode);
        CollectionAssert.AreEqual(
            CancelOnly,
            request.AvailableTransitions.ToArray());
    }

    [TestMethod]
    public async Task OneUsersRequestIsInvisibleAndUntouchableToAnother()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(owner, "a-wrinkle-in-time");
        var request = await CreateAndReadRequestAsync(
            owner,
            workId,
            ["Audiobook"]);

        // A different account — and an administrator at that, so this also shows
        // the admin queue is not reachable through the requester's own routes.
        using var other = await fixture.CreateAdminClientAsync();
        var otherToken = await WebTestFixture.GetAntiforgeryTokenAsync(other);
        other.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, otherToken);

        var theirList = await other.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        var cancelAttempt = await TransitionAsync(other, request.Id, "Cancelled", null);

        Assert.IsNotNull(theirList);
        Assert.IsFalse(theirList.Active.Any(item => item.Id == request.Id));
        Assert.IsFalse(theirList.History.Any(item => item.Id == request.Id));

        // 404, not 403: answering "forbidden" would confirm the request exists.
        Assert.AreEqual(HttpStatusCode.NotFound, cancelAttempt.StatusCode);

        var stillMine = await owner.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(stillMine);
        Assert.AreEqual(
            "PendingAcquisition",
            stillMine.Active.Single(item => item.Id == request.Id).Status);
    }

    [TestMethod]
    public async Task ARequestSurvivesARestartOfTheHost()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        var workId = await ResolveWorkAsync(client, "project-hail-mary");
        var request = await CreateAndReadRequestAsync(
            client,
            workId,
            ["Ebook"]);

        await using var restarted = fixture.RestartHost();
        using var afterRestart = await restarted.CreateUserClientAsync();

        var mine = await afterRestart.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");

        Assert.IsNotNull(mine);
        var found = mine.Active.SingleOrDefault(item => item.Id == request.Id);
        Assert.IsNotNull(found, "The request did not survive the restart.");
        Assert.AreEqual("Project Hail Mary", found.WorkTitle);
    }

    [TestMethod]
    public async Task ConcurrentPeopleShareOneRequestAndKeepNotesPrivate()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var reader = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var workId = await ResolveWorkAsync(reader, "the-hobbit");

        var responses = await Task.WhenAll(
            CreateRequestAsync(reader, workId, ["Ebook"], "Reader private note"),
            CreateRequestAsync(admin, workId, ["Ebook"], "Admin private note"));
        foreach (var response in responses) Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var first = await responses[0].Content.ReadFromJsonAsync<BookRequestResponse>();
        var second = await responses[1].Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(first.Formats.Single().FormatId, second.Formats.Single().FormatId);
        Assert.AreEqual("Reader private note", first.Note);
        Assert.AreEqual("Admin private note", second.Note);

        var mine = await reader.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var current = mine.Active.Single(request => request.Id == first.Id);
        Assert.AreEqual(2, current.RequesterCount);
        var withdrawn = await TransitionAsync(reader, first.Id, "Cancelled", null);
        Assert.AreEqual(HttpStatusCode.OK, withdrawn.StatusCode);
        var readerState = await withdrawn.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(readerState);
        Assert.AreEqual("Cancelled", readerState.Status);
        var theirs = await admin.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(theirs);
        Assert.AreEqual("PendingAcquisition", theirs.Active.Single(request => request.Id == first.Id).Status);

        var rejoined = await CreateAndReadRequestAsync(reader, workId, ["Ebook"]);
        Assert.AreEqual(first.Id, rejoined.Id);
        Assert.AreEqual(2, rejoined.RequesterCount);
        var queue = await admin.GetFromJsonAsync<AdminBookRequestResponse>($"/api/v1/admin/requests/{first.Id}");
        Assert.IsNotNull(queue);
        Assert.IsNotNull(queue.Participants);
        Assert.HasCount(2, queue.Participants);
        var unavailable = await admin.PostAsJsonAsync($"/api/v1/admin/requests/{first.Id}/transitions",
            new ChangeBookRequestStatusRequest("NotAvailable", "No suitable copy found", queue.Request.Version));
        Assert.AreEqual(HttpStatusCode.OK, unavailable.StatusCode);
        foreach (var client in new[] { reader, admin })
        {
            var feed = await client.GetFromJsonAsync<NotificationListResponse>("/api/v1/notifications/");
            Assert.IsNotNull(feed);
            Assert.IsTrue(feed.Notifications.Any(notification => notification.SubjectId == first.Id.ToString()));
        }
    }

    [TestMethod]
    public async Task AVersionExceptionCannotBeRequeuedByBulkOrIndividualAdminActions()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var reader = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var workId = await ResolveWorkAsync(reader, "the-hobbit");
        var response = await reader.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false, "Language", "Spanish translation"));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var version = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(version);
        var bulk = await admin.PostAsJsonAsync("/api/v1/admin/requests/recheck", new RecheckNeedsReviewRequest(null));
        Assert.AreEqual(HttpStatusCode.OK, bulk.StatusCode);
        var review = await admin.GetFromJsonAsync<AdminBookRequestResponse>($"/api/v1/admin/requests/{version.Id}");
        Assert.IsNotNull(review);
        Assert.AreEqual("NeedsReview", review.Request.Status);
        Assert.IsFalse(review.Request.AvailableTransitions.Contains("PendingAcquisition"));
        var requeue = await admin.PostAsJsonAsync($"/api/v1/admin/requests/{version.Id}/transitions",
            new ChangeBookRequestStatusRequest("PendingAcquisition", "Try again", review.Request.Version));
        Assert.AreEqual(HttpStatusCode.BadRequest, requeue.StatusCode);
    }

    [TestMethod]
    public async Task ADeliveredEbookFormatChipLinksToItsCwaBookPage()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await ConfigureCwaAsync(admin);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Ebook"]);
        var formatId = request.Formats.Single(format => format.MediaType == "Ebook").FormatId;

        await SeedEbookDeliveredAsync(fixture, request.Id, formatId, workId, "42", Now);

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var found = mine.Active.Concat(mine.History).Single(item => item.Id == request.Id);
        var ebook = found.Formats.Single(format => format.MediaType == "Ebook");
        Assert.AreEqual("https://cwa.example.test/book/42", ebook.ExternalActionUri);
    }

    [TestMethod]
    public async Task ADeliveredAudiobookFormatChipLinksToItsAudiobookshelfItemPage()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await ConfigureAudiobookshelfAsync(admin);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Audiobook"]);
        var formatId = request.Formats.Single(format => format.MediaType == "Audiobook").FormatId;

        await SeedAudiobookDeliveredAsync(fixture, request.Id, formatId, workId, "li_123", Now, bundled: false);

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var found = mine.Active.Concat(mine.History).Single(item => item.Id == request.Id);
        var audiobook = found.Formats.Single(format => format.MediaType == "Audiobook");
        Assert.AreEqual("https://audio.example.test/item/li_123", audiobook.ExternalActionUri);
    }

    [TestMethod]
    public async Task AChapteredAudiobookBundleStillResolvesItsDeliveredLink()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await ConfigureAudiobookshelfAsync(admin);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Audiobook"]);
        var formatId = request.Formats.Single(format => format.MediaType == "Audiobook").FormatId;

        await SeedAudiobookDeliveredAsync(fixture, request.Id, formatId, workId, "li_bundle", Now, bundled: true);

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var found = mine.Active.Concat(mine.History).Single(item => item.Id == request.Id);
        var audiobook = found.Formats.Single(format => format.MediaType == "Audiobook");
        Assert.AreEqual("https://audio.example.test/item/li_bundle", audiobook.ExternalActionUri);
    }

    [TestMethod]
    public async Task ANonTerminalImportNeverExposesALink()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await ConfigureCwaAsync(admin);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Ebook"]);
        var formatId = request.Formats.Single(format => format.MediaType == "Ebook").FormatId;

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asset = new MediaAsset(workId, null, RequestMediaType.Ebook, ".epub", "book.epub",
                "book.epub", 100, new string('a', 64), "application/epub+zip", formatId, null, Now);
            var import = new LibraryImport(asset.Id, Now);
            import.MarkAwaitingVerification("book-target.epub");
            database.MediaAssets.Add(asset);
            database.LibraryImports.Add(import);
            await database.SaveChangesAsync();
        }

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var found = mine.Active.Concat(mine.History).Single(item => item.Id == request.Id);
        var ebook = found.Formats.Single(format => format.MediaType == "Ebook");
        Assert.IsNull(ebook.ExternalActionUri);
    }

    [TestMethod]
    public async Task AnAvailableFormatWithNoPublishedAssetHasNoLink()
    {
        // Covers a format marked Available outside this publishing pipeline
        // (e.g. an owned-import confirmation) -- there is no MediaAsset row
        // to resolve a link from, so this must degrade quietly, not throw.
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateRequestingClientAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        await ConfigureCwaAsync(admin);
        var workId = await ResolveWorkAsync(client, "the-hobbit");
        var request = await CreateAndReadRequestAsync(client, workId, ["Ebook"]);
        var formatId = request.Formats.Single(format => format.MediaType == "Ebook").FormatId;

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await database.BookRequests
                .Include(row => row.Participants)
                .Include(row => row.Formats)
                .SingleAsync(row => row.Id == request.Id);
            owner.MarkFormatAvailable(formatId, Now);
            await database.SaveChangesAsync();
        }

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var found = mine.Active.Concat(mine.History).Single(item => item.Id == request.Id);
        var ebook = found.Formats.Single(format => format.MediaType == "Ebook");
        Assert.IsNull(ebook.ExternalActionUri);
    }

    private static async Task SeedEbookDeliveredAsync(
        WebTestFixture fixture, Guid requestId, Guid formatId, Guid workId, string externalBookId, DateTimeOffset atUtc)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await database.BookRequests
            .Include(row => row.Participants)
            .Include(row => row.Formats)
            .SingleAsync(row => row.Id == requestId);
        var asset = new MediaAsset(workId, null, RequestMediaType.Ebook, ".epub", "book.epub",
            "book.epub", 100, new string('a', 64), "application/epub+zip", formatId, null, atUtc);
        var import = new LibraryImport(asset.Id, atUtc);
        import.MarkAvailable(externalBookId, atUtc);
        owner.MarkFormatAvailable(formatId, atUtc);
        database.MediaAssets.Add(asset);
        database.LibraryImports.Add(import);
        await database.SaveChangesAsync();
    }

    private static async Task SeedAudiobookDeliveredAsync(
        WebTestFixture fixture, Guid requestId, Guid formatId, Guid workId, string externalItemId,
        DateTimeOffset atUtc, bool bundled)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await database.BookRequests
            .Include(row => row.Participants)
            .Include(row => row.Formats)
            .SingleAsync(row => row.Id == requestId);

        if (bundled)
        {
            var bundleId = Guid.NewGuid();
            var first = new MediaAsset(workId, null, RequestMediaType.Audiobook, ".m4b", "part1.m4b",
                "part1.m4b", 100, new string('a', 64), "audio/mp4", formatId, null, atUtc, bundleId, 1, 2);
            var second = new MediaAsset(workId, null, RequestMediaType.Audiobook, ".m4b", "part2.m4b",
                "part2.m4b", 100, new string('b', 64), "audio/mp4", formatId, null, atUtc, bundleId, 2, 2);
            var delivery = AudiobookshelfDelivery.ForBundle(bundleId, atUtc);
            delivery.MarkDelivered(externalItemId, atUtc);
            database.MediaAssets.AddRange(first, second);
            database.Deliveries.Add(delivery);
        }
        else
        {
            var asset = new MediaAsset(workId, null, RequestMediaType.Audiobook, ".m4b", "book.m4b",
                "book.m4b", 100, new string('a', 64), "audio/mp4", formatId, null, atUtc);
            var delivery = new AudiobookshelfDelivery(asset.Id, atUtc);
            delivery.MarkDelivered(externalItemId, atUtc);
            database.MediaAssets.Add(asset);
            database.Deliveries.Add(delivery);
        }

        owner.MarkFormatAvailable(formatId, atUtc);
        await database.SaveChangesAsync();
    }

    private static async Task ConfigureAudiobookshelfAsync(HttpClient client)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/",
            new FamilyLibrarian.Contracts.Publishing.SetAudiobookshelfSettingsRequest(
                "https://audio.example.test", null, "lib-1", "folder-1"));
        settings.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/enabled",
            new FamilyLibrarian.Contracts.Publishing.SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> CreateRequestingClientAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    /// <summary>
    /// Turns a deterministic demo candidate into a canonical Work, the same way
    /// the browser does before offering a request action.
    /// </summary>
    private static async Task<Guid> ResolveWorkAsync(HttpClient client, string externalId)
    {
        var response = await client.PostAsync(
            $"/api/v1/catalog/candidates/demo/{externalId}/resolve",
            content: null);
        response.EnsureSuccessStatusCode();

        var work = await response.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        return await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id);
    }

    private static Task<HttpResponseMessage> CreateRequestAsync(
        HttpClient client,
        Guid workId,
        string[] formats,
        string? note,
        bool confirmDuplicate = false,
        bool confirmOwned = false) =>
        client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, formats, note, confirmDuplicate, confirmOwned));

    private static async Task<BookRequestResponse> CreateAndReadRequestAsync(
        HttpClient client,
        Guid workId,
        string[] formats,
        bool confirmDuplicate = false,
        bool confirmOwned = false)
    {
        var response = await CreateRequestAsync(client, workId, formats, null, confirmDuplicate, confirmOwned);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var request = await response.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        return request;
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task ConfigureCwaAsync(HttpClient client)
    {
        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new FamilyLibrarian.Contracts.Publishing.SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey", "https://cwa.example.test", null, null, null));
        settings.EnsureSuccessStatusCode();

        // Enabling requires a passing connection test for the saved configuration
        // (docs/01 §12.1.1) -- FamilyLibrarianAppFactory registers a default-safe
        // ICwaConnectionTester double, so this succeeds without a reachable CWA.
        var test = await client.PostAsJsonAsync("/api/v1/admin/publishing/cwa/test", new { });
        test.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled",
            new FamilyLibrarian.Contracts.Publishing.SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private sealed class DeterministicCatalogClient(string? bookId) : ICwaCatalogClient
    {
        public Task<BookMatchResult> FindBookIdAsync(
            string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken) =>
            Task.FromResult(bookId is null
                ? BookMatchResult.NoMatchResult
                : BookMatchResult.Match(new CandidateBook(bookId, title, author)));
    }

    /// <summary>
    /// Asserts a status code and reports the response body when it does not
    /// match, so a failure names the reason instead of only the number.
    /// </summary>
    private static async Task AssertStatusAsync(
        HttpStatusCode expected,
        HttpResponseMessage response)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        Assert.Fail(
            $"Expected {expected} but got {response.StatusCode} for " +
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: " +
            await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> TransitionAsync(
        HttpClient client,
        Guid requestId,
        string status,
        string? reason) =>
        client.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/transitions",
            new ChangeBookRequestStatusRequest(status, reason));
}
