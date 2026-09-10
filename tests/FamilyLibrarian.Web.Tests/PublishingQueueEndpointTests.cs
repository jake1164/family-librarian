using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Delivery;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the end-to-end publishing pipeline through the real HTTP endpoints:
/// approving a manually imported asset triggers a publish attempt, the result
/// appears in the admin queue, and Recheck can move it forward.
/// </summary>
[TestClass]
public sealed class PublishingQueueEndpointTests
{
    [TestMethod]
    public async Task PersonalHistoryIsPrivateAndAdminsCanSupportReportedMissingStandaloneBooks()
    {
        await using var isolated = await WebTestFixture.CreateAsync();
        var fixture = WebTestFixture.Require(isolated);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IEbookDeliveryProvider>();
            services.AddSingleton<IEbookDeliveryProvider>(new DeterministicEbookDeliveryProvider());
        });
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        using var owner = await fixture.CreateUserClientAsync();
        owner.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(owner));
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await database.Users.SingleAsync(row => row.Email == WebTestFixture.UserEmail);
            var target = new DeliveryTarget(user.Id, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.com", DateTimeOffset.UtcNow);
            var attempt = new DeliveryAttempt(null, user.Id, target.Id, "cwa", "personal-42", "epub", true, 1,
                DateTimeOffset.UtcNow, "A standalone library book");
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, DateTimeOffset.UtcNow);
            attempt.TransitionTo(DeliveryAttemptStatus.Submitted, DateTimeOffset.UtcNow);
            database.DeliveryTargets.Add(target);
            database.DeliveryAttempts.Add(attempt);
            await database.SaveChangesAsync();
            id = attempt.Id;
        }
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/me/delivery/kindle/attempts")).StatusCode);
        owner.DefaultRequestHeaders.Remove(AntiforgeryTokenEndpoint.HeaderName);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await owner.PostAsync($"/api/v1/me/delivery/kindle/attempts/{id}/report-missing", null)).StatusCode);
        owner.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(owner));
        var mine = await owner.GetFromJsonAsync<PersonalDeliveryAttemptResponse[]>("/api/v1/me/delivery/kindle/attempts");
        Assert.IsNotNull(mine);
        Assert.AreEqual("A standalone library book", mine.Single(item => item.Id == id).BookTitle);
        Assert.IsNull(mine.Single(item => item.Id == id).RequestId);
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/me/delivery/kindle/attempts/{id}")).StatusCode);
        var adminsOwnHistory = await admin.GetFromJsonAsync<PersonalDeliveryAttemptResponse[]>("/api/v1/me/delivery/kindle/attempts");
        Assert.IsFalse(adminsOwnHistory!.Any(item => item.Id == id));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/admin/publishing/queue")).StatusCode);

        (await owner.PostAsync($"/api/v1/me/delivery/kindle/attempts/{id}/report-missing", null)).EnsureSuccessStatusCode();
        var queue = await admin.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var missing = queue!.DeliveryAttempts.Single(item => item.Id == id);
        Assert.AreEqual("A standalone library book", missing.WorkTitle);
        Assert.AreEqual("Submitted", missing.Status);
        Assert.AreEqual("ReportedMissing", missing.ConfirmationStatus);
        Assert.IsNotNull(missing.ConfirmedAtUtc);
        Assert.IsTrue(missing.CanRetry);
        Assert.IsTrue(missing.IsLatest);

        (await admin.PostAsync($"/api/v1/admin/publishing/delivery-attempts/{id}/retry", null)).EnsureSuccessStatusCode();
        queue = await admin.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var history = queue!.DeliveryAttempts.Where(item => item.DeliveryId == missing.DeliveryId).ToArray();
        Assert.HasCount(2, history);
        Assert.IsFalse(history.Single(item => item.Id == id).IsLatest);
        Assert.IsFalse(history.Single(item => item.Id == id).CanRetry);
        var latest = history.Single(item => item.IsLatest);
        Assert.AreEqual("Unconfirmed", latest.ConfirmationStatus);
        var detail = await owner.GetFromJsonAsync<PersonalDeliveryAttemptResponse>($"/api/v1/me/delivery/kindle/attempts/{id}");
        Assert.AreEqual(latest.Id, detail!.LatestAttemptId);
        (await owner.PostAsync($"/api/v1/me/delivery/kindle/attempts/{latest.Id}/confirm-received", null)).EnsureSuccessStatusCode();
        queue = await admin.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.AreEqual("Confirmed", queue!.DeliveryAttempts.Single(item => item.Id == latest.Id).ConfirmationStatus);
        Assert.IsFalse(queue.DeliveryAttempts.Single(item => item.Id == latest.Id).CanRetry);
    }

    [TestMethod]
    public async Task SupportHistoryShowsCooldownAndExhaustionOnlyForTheLatestAttempt()
    {
        await using var isolated = await WebTestFixture.CreateAsync();
        var fixture = WebTestFixture.Require(isolated);
        using var admin = await fixture.CreateAdminClientAsync();
        Guid deliveryId;
        Guid waitingId;
        var now = DateTimeOffset.UtcNow;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await database.Users.SingleAsync(row => row.Email == FamilyLibrarianAppFactory.AdminEmail);
            var target = new DeliveryTarget(user.Id, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.com", now);
            database.DeliveryTargets.Add(target);
            deliveryId = Guid.NewGuid();
            for (var number = 1; number <= 3; number++)
            {
                var attempt = new DeliveryAttempt(null, user.Id, target.Id, "cwa", "exhausted", "epub", false, number, now,
                    deliveryId: deliveryId);
                attempt.TransitionTo(DeliveryAttemptStatus.Submitting, now);
                attempt.TransitionTo(DeliveryAttemptStatus.Failed, now, "offline", retryable: true);
                database.DeliveryAttempts.Add(attempt);
            }
            var waiting = new DeliveryAttempt(null, user.Id, target.Id, "cwa", "cooldown", "epub", false, 1, now);
            waiting.TransitionTo(DeliveryAttemptStatus.Submitting, now);
            waiting.TransitionTo(DeliveryAttemptStatus.Failed, now, "offline", retryable: true);
            database.DeliveryAttempts.Add(waiting);
            waitingId = waiting.Id;
            await database.SaveChangesAsync();
        }
        var queue = await admin.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var rows = queue!.DeliveryAttempts.Where(item => item.DeliveryId == deliveryId).ToArray();
        Assert.HasCount(3, rows);
        Assert.IsTrue(rows.Single(item => item.IsLatest).AutomaticRetriesExhausted);
        Assert.IsTrue(rows.Where(item => !item.IsLatest).All(item => !item.CanRetry && item.NextAutomaticRetryAtUtc is null));
        Assert.IsNull(rows.Single(item => item.IsLatest).NextAutomaticRetryAtUtc);
        var next = queue.DeliveryAttempts.Single(item => item.Id == waitingId).NextAutomaticRetryAtUtc;
        Assert.IsNotNull(next);
        Assert.IsTrue(Math.Abs((next.Value - now.AddMinutes(2)).TotalMilliseconds) < 1);
    }

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
    public async Task AnApprovedEbookAppearsInTheQueueAwaitingVerificationByDefault()
    {
        // The shared fixture's default fakes (AlwaysSucceedsCwaIngestTransport,
        // AlwaysEmptyCwaCatalogClient) mean the handoff always succeeds but the
        // immediate catalog check never finds it — exactly the "asynchronous
        // ingest, nothing confirmed yet" case Recheck exists for.
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureCwaAsync(client);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);
        var assetId = await ManualImportAndApproveAsync(client, requestId, formatId);

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queue);
        var import = queue.LibraryImports.SingleOrDefault(entry => entry.RequestId == requestId);
        Assert.IsNotNull(import, "The approved ebook should have a library-import queue entry.");
        Assert.AreEqual("AwaitingVerification", import.Status);
        Assert.IsNull(import.ExternalBookId);

        // Recheck with the still-empty catalog fake leaves it exactly where it was.
        var recheck = await client.PostAsync($"/api/v1/admin/publishing/library-imports/{import.Id}/recheck", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, recheck.StatusCode);

        var queueAfter = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var importAfter = queueAfter!.LibraryImports.Single(entry => entry.Id == import.Id);
        Assert.AreEqual("AwaitingVerification", importAfter.Status);
    }

    [TestMethod]
    public async Task RecheckMarksTheImportAvailableOnceTheCatalogFindsIt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient(bookIdOnFirstCall: null));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureCwaAsync(client);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);
        await ManualImportAndApproveAsync(client, requestId, formatId);

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var import = queue!.LibraryImports.Single(entry => entry.RequestId == requestId);
        Assert.AreEqual("AwaitingVerification", import.Status);

        // Flip the fake to now report the book found, then recheck.
        var catalogClient = (DeterministicCatalogClient)factory.Services.GetRequiredService<ICwaCatalogClient>();
        catalogClient.NextBookId = "123";

        var recheck = await client.PostAsync($"/api/v1/admin/publishing/library-imports/{import.Id}/recheck", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, recheck.StatusCode);

        // The queue is a status view, not a to-do list: an Available entry stays
        // visible (with its book id) rather than disappearing, since there is no
        // other admin surface where a successful publish shows up.
        var queueAfter = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var importAfter = queueAfter!.LibraryImports.SingleOrDefault(entry => entry.Id == import.Id);
        Assert.IsNotNull(importAfter);
        Assert.AreEqual("Available", importAfter.Status);
        Assert.AreEqual("123", importAfter.ExternalBookId);
    }

    [TestMethod]
    public async Task AnApprovedAudiobookIsDeliveredImmediatelyByDefault()
    {
        // AlwaysEmptyAudiobookshelfApiClient reports no existing item, then a
        // successful upload with a generated id — the ordinary "clean" path.
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureAudiobookshelfAsync(client);
        var (requestId, formatId) = await CreateAudiobookRequestAsync(client);
        await ManualImportAudiobookAndApproveAsync(client, requestId, formatId);

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queue);
        var delivery = queue.Deliveries.SingleOrDefault(entry => entry.RequestId == requestId);
        Assert.IsNotNull(delivery, "The approved audiobook should have a delivery queue entry.");
        Assert.AreEqual("Delivered", delivery.Status);
        Assert.IsNotNull(delivery.ExternalItemId);
    }

    [TestMethod]
    public async Task ARequesterSeesTheirKindleDeliveryStatusOnceTheEbookIsSent()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient(bookIdOnFirstCall: null));
                services.RemoveAll<IEbookDeliveryProvider>();
                services.AddSingleton<IEbookDeliveryProvider>(new DeterministicEbookDeliveryProvider());
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureCwaAsync(client);

        var kindleSet = await client.PutAsJsonAsync(
            "/api/v1/me/delivery/kindle", new SetKindleAddressRequest("reader@kindle.example", null, true));
        Assert.AreEqual(HttpStatusCode.OK, kindleSet.StatusCode);
        var target = await kindleSet.Content.ReadFromJsonAsync<DeliveryTargetResponse>();
        Assert.IsNotNull(target);

        var resolve = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var workId = await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id);

        // The catalog client must still report "not found" here, or request
        // creation would answer with an OwnedWarning conflict instead of
        // creating a PendingAcquisition request -- it only starts reporting a
        // match once manual-import needs to verify the just-published file.
        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false, DeliveryTargetId: target.Id));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);
        Assert.AreEqual(target.Id, request.DeliveryTargetId);
        var formatId = request.Formats.Single().FormatId;

        var catalogClient = (DeterministicCatalogClient)factory.Services.GetRequiredService<ICwaCatalogClient>();
        catalogClient.NextBookId = "42";

        await ManualImportAndApproveAsync(client, request.Id, formatId);

        var mine = await client.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.IsNotNull(mine);
        var updated = mine.Active.Concat(mine.History).Single(item => item.Id == request.Id);
        Assert.IsNotNull(updated.KindleDelivery, "The request should show a Kindle delivery status.");
        Assert.AreEqual("Submitted", updated.KindleDelivery!.Status);
    }

    /// <summary>
    /// Beta plan §26 "Two Users / Both Delivery": sharing one request must not
    /// duplicate acquisition/ingest, and each opted-in participant must get
    /// their own delivery to their own target -- never the other's.
    /// </summary>
    [TestMethod]
    public async Task TwoParticipantsWhoBothOptInEachGetOneIndependentKindleDeliveryFromOneAcquisition()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient(bookIdOnFirstCall: null));
                services.RemoveAll<IEbookDeliveryProvider>();
                services.AddSingleton<IEbookDeliveryProvider>(new DeterministicEbookDeliveryProvider());
            });
        // The admin account and the fixture's fixed regular user are the only
        // two distinct real identities the harness provides -- sufficient here
        // since what matters is that they are different UserIds, not roles.
        using var userA = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(userA);
        userA.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(userA));
        using var userB = await fixture.CreateUserClientAsync();
        userB.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(userB));

        await ConfigureCwaAsync(userA);
        var targetA = await SetKindleTargetAsync(userA, "reader-a@kindle.example");
        var targetB = await SetKindleTargetAsync(userB, "reader-b@kindle.example");

        var resolve = await userA.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var workId = await fixture.CopyWorkForTestAsync(work.Id);

        var createdA = await userA.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false, DeliveryTargetId: targetA.Id));
        Assert.AreEqual(HttpStatusCode.Created, createdA.StatusCode);
        var requestA = await createdA.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(requestA);

        var createdB = await userB.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false, DeliveryTargetId: targetB.Id));
        Assert.AreEqual(HttpStatusCode.Created, createdB.StatusCode);
        var requestB = await createdB.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(requestB);

        // Both requests must resolve to the same shared aggregate, or the rest
        // of this test would trivially pass by acquiring the book twice.
        Assert.AreEqual(requestA.Id, requestB.Id);

        var catalogClient = (DeterministicCatalogClient)factory.Services.GetRequiredService<ICwaCatalogClient>();
        catalogClient.NextBookId = "shared-42";
        var formatId = requestA.Formats.Single().FormatId;
        await ManualImportAndApproveAsync(userA, requestA.Id, formatId);

        var queue = await userA.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queue);
        Assert.HasCount(1, queue.LibraryImports.Where(import => import.RequestId == requestA.Id),
            "Sharing one request must acquire and ingest the book exactly once.");

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await database.DeliveryAttempts.Where(attempt => attempt.RequestId == requestA.Id).ToArrayAsync();
        Assert.HasCount(2, attempts, "Each opted-in participant should get exactly one delivery attempt.");

        var userAId = (await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail)).Id;
        var userBId = (await database.Users.SingleAsync(user => user.Email == WebTestFixture.UserEmail)).Id;
        var attemptA = attempts.Single(attempt => attempt.UserId == userAId);
        var attemptB = attempts.Single(attempt => attempt.UserId == userBId);
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, attemptA.Status);
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, attemptB.Status);
        Assert.AreEqual(targetA.Id, attemptA.DeliveryTargetId);
        Assert.AreEqual(targetB.Id, attemptB.DeliveryTargetId);
    }

    /// <summary>
    /// Beta plan §26 "Two Users / One Delivery": the participant who never
    /// opted in must receive zero Kindle sends, not a copy of the other
    /// participant's.
    /// </summary>
    [TestMethod]
    public async Task OnlyTheParticipantWhoOptedInReceivesAKindleDeliveryFromASharedRequest()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient(bookIdOnFirstCall: null));
                services.RemoveAll<IEbookDeliveryProvider>();
                services.AddSingleton<IEbookDeliveryProvider>(new DeterministicEbookDeliveryProvider());
            });
        using var userA = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(userA);
        userA.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(userA));
        using var userB = await fixture.CreateUserClientAsync();
        userB.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(userB));

        await ConfigureCwaAsync(userA);
        var targetA = await SetKindleTargetAsync(userA, "reader-a@kindle.example");

        var resolve = await userA.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var workId = await fixture.CopyWorkForTestAsync(work.Id);

        var createdA = await userA.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false, DeliveryTargetId: targetA.Id));
        Assert.AreEqual(HttpStatusCode.Created, createdA.StatusCode);
        var requestA = await createdA.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(requestA);

        // User B joins the same request wanting the ebook, but never
        // configures or supplies a Kindle target.
        var createdB = await userB.PostAsJsonAsync("/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, createdB.StatusCode);
        var requestB = await createdB.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(requestB);
        Assert.AreEqual(requestA.Id, requestB.Id);

        var catalogClient = (DeterministicCatalogClient)factory.Services.GetRequiredService<ICwaCatalogClient>();
        catalogClient.NextBookId = "shared-43";
        var formatId = requestA.Formats.Single().FormatId;
        await ManualImportAndApproveAsync(userA, requestA.Id, formatId);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await database.DeliveryAttempts.Where(attempt => attempt.RequestId == requestA.Id).ToArrayAsync();
        Assert.HasCount(1, attempts, "The participant who never opted in must not receive a Kindle send.");

        var userAId = (await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail)).Id;
        Assert.AreEqual(userAId, attempts.Single().UserId);
        Assert.AreEqual(targetA.Id, attempts.Single().DeliveryTargetId);
    }

    /// <summary>
    /// Idempotent create-or-correct: the shared class-level fixture persists
    /// data across tests in this class, so a fixed identity (e.g. the admin
    /// account, reused as "User A" across these tests) may already have a
    /// target from an earlier test -- a bare create would then 409.
    /// </summary>
    private static async Task<DeliveryTargetResponse> SetKindleTargetAsync(HttpClient client, string address)
    {
        var existing = await client.GetAsync("/api/v1/me/delivery/kindle");
        uint? expectedVersion = null;
        if (existing.StatusCode == HttpStatusCode.OK)
        {
            var current = await existing.Content.ReadFromJsonAsync<DeliveryTargetResponse>();
            Assert.IsNotNull(current);
            if (current.Address == address && current.IsEnabled)
            {
                return current;
            }
            expectedVersion = current.Version;
        }

        var response = await client.PutAsJsonAsync(
            "/api/v1/me/delivery/kindle", new SetKindleAddressRequest(address, expectedVersion, true));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var target = await response.Content.ReadFromJsonAsync<DeliveryTargetResponse>();
        Assert.IsNotNull(target);

        if (!target.IsEnabled)
        {
            var enabled = await client.PutAsJsonAsync(
                "/api/v1/me/delivery/kindle/enabled", new SetKindleEnabledRequest(true, target.Version));
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
            target = await enabled.Content.ReadFromJsonAsync<DeliveryTargetResponse>();
            Assert.IsNotNull(target);
        }

        return target;
    }

    [TestMethod]
    public async Task TheAdminQueueShowsKindleDeliveryAttemptsAndSupportsRetry()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IEbookDeliveryProvider>();
                services.AddSingleton<IEbookDeliveryProvider>(new DeterministicEbookDeliveryProvider());
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        Guid failedAttemptId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await database.Users.SingleAsync(candidate => candidate.Email == FamilyLibrarianAppFactory.AdminEmail);
            var deliveryTarget = new DeliveryTarget(
                user.Id, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.example", DateTimeOffset.UtcNow);
            database.DeliveryTargets.Add(deliveryTarget);

            // The existing-book fast path -- no BookRequest at all -- exercises
            // the admin queue's LEFT JOIN to BookRequest/Work.
            var attempt = new DeliveryAttempt(
                requestId: null, user.Id, deliveryTarget.Id, "cwa", "book-42", "epub",
                convert: true, attemptNumber: 1, DateTimeOffset.UtcNow);
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, DateTimeOffset.UtcNow);
            attempt.TransitionTo(DeliveryAttemptStatus.Failed, DateTimeOffset.UtcNow, "timeout", retryable: true);
            database.DeliveryAttempts.Add(attempt);

            await database.SaveChangesAsync();
            failedAttemptId = attempt.Id;
        }

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queue);
        var entry = queue.DeliveryAttempts.SingleOrDefault(item => item.Id == failedAttemptId);
        Assert.IsNotNull(entry, "The seeded Kindle attempt should appear in the admin queue.");
        Assert.IsNull(entry.RequestId);
        Assert.IsNull(entry.WorkId);
        Assert.IsNull(entry.WorkTitle);
        Assert.AreEqual("Failed", entry.Status);
        Assert.AreEqual("book-42", entry.ExternalBookId);

        var retry = await client.PostAsync($"/api/v1/admin/publishing/delivery-attempts/{failedAttemptId}/retry", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, retry.StatusCode);

        var queueAfter = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queueAfter);
        var retried = queueAfter.DeliveryAttempts.SingleOrDefault(
            item => item.AttemptNumber == 2 && item.ExternalBookId == "book-42");
        Assert.IsNotNull(retried, "A retry should create a new attempt row.");
        Assert.AreEqual("Submitted", retried.Status);
    }

    [TestMethod]
    public async Task UnknownSendRequiresAcknowledgementAndConcurrentExplicitResendsCreateOneSuccessor()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IEbookDeliveryProvider>();
            services.AddSingleton<IEbookDeliveryProvider>(new DeterministicEbookDeliveryProvider());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        Guid id;
        Guid deliveryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await database.Users.SingleAsync(row => row.Email == FamilyLibrarianAppFactory.AdminEmail);
            var target = new DeliveryTarget(user.Id, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.com", DateTimeOffset.UtcNow);
            var attempt = new DeliveryAttempt(null, user.Id, target.Id, "cwa", "42", "epub", false, 1, DateTimeOffset.UtcNow);
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, DateTimeOffset.UtcNow);
            attempt.TransitionTo(DeliveryAttemptStatus.SubmissionUnknown, DateTimeOffset.UtcNow);
            database.DeliveryTargets.Add(target);
            database.DeliveryAttempts.Add(attempt);
            await database.SaveChangesAsync();
            id = attempt.Id;
            deliveryId = attempt.DeliveryId;
        }
        var noAcknowledgement = await client.PostAsync($"/api/v1/me/delivery/kindle/attempts/{id}/retry", null);
        Assert.AreEqual(HttpStatusCode.Conflict, noAcknowledgement.StatusCode);
        var adminNoAcknowledgement = await client.PostAsync($"/api/v1/admin/publishing/delivery-attempts/{id}/retry", null);
        Assert.AreEqual(HttpStatusCode.NotFound, adminNoAcknowledgement.StatusCode);
        using var otherUser = await fixture.CreateUserClientAsync();
        otherUser.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(otherUser));
        var denied = await otherUser.PostAsync($"/api/v1/me/delivery/kindle/attempts/{id}/retry?confirmPossibleDuplicate=true", null);
        Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);

        var retries = await Task.WhenAll(
            client.PostAsync($"/api/v1/me/delivery/kindle/attempts/{id}/retry?confirmPossibleDuplicate=true", null),
            client.PostAsync($"/api/v1/admin/publishing/delivery-attempts/{id}/retry?confirmPossibleDuplicate=true", null));
        Assert.IsTrue(retries.All(response => response.IsSuccessStatusCode));
        foreach (var response in retries) response.Dispose();
        await using var verify = factory.Services.CreateAsyncScope();
        var attempts = await verify.ServiceProvider.GetRequiredService<AppDbContext>().DeliveryAttempts
            .Where(row => row.DeliveryId == deliveryId).ToArrayAsync();
        Assert.HasCount(2, attempts);
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, attempts.Single(row => row.AttemptNumber == 2).Status);
    }

    private static async Task ConfigureCwaAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey",
                "https://cwa.example.test", null, null, null));
        response.EnsureSuccessStatusCode();

        // Enabling requires a passing connection test for the saved configuration
        // (docs/01 §12.1.1) -- FamilyLibrarianAppFactory registers a default-safe
        // ICwaConnectionTester double, so this succeeds without a reachable CWA.
        var test = await client.PostAsJsonAsync("/api/v1/admin/publishing/cwa/test", new { });
        test.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled", new SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task ConfigureAudiobookshelfAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/",
            new SetAudiobookshelfSettingsRequest("https://abs.example.test", null, "lib-1", "folder-1"));
        response.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/enabled", new SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task SignInAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The uploaded file's title/author match the-hobbit's catalog metadata
    // (ebook) or has no identity verifier at all (audiobook — see
    // EpubAssetIdentityVerifier.Supports), so a clean scan is approved by
    // policy and published during the upload itself; no separate admin
    // approval call is needed.
    private static async Task<Guid> ManualImportAndApproveAsync(HttpClient client, Guid requestId, Guid formatId)
    {
        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        return imported.MediaAssetId;
    }

    private static async Task<Guid> ManualImportAudiobookAndApproveAsync(HttpClient client, Guid requestId, Guid formatId)
    {
        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalMp3Bytes(), "book.mp3"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        return imported.MediaAssetId;
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private static async Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(HttpClient client) =>
        await CreateRequestAsync(client, "Ebook");

    private static async Task<(Guid RequestId, Guid FormatId)> CreateAudiobookRequestAsync(HttpClient client) =>
        await CreateRequestAsync(client, "Audiobook");

    private static async Task<(Guid RequestId, Guid FormatId)> CreateRequestAsync(HttpClient client, string mediaType)
    {
        var resolve = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), [mediaType], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == mediaType);
        return (request.Id, format.FormatId);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();

    private static byte[] BuildMinimalMp3Bytes()
    {
        // Beyond SignatureFileTypeDetector's magic-byte sniff, AudioValidator
        // now requires two chained, structurally valid MPEG frames -- a bare
        // ID3 header with no real frame behind it (the previous fixture) no
        // longer passes. MPEG1 Audio Layer III, bitrate index 9 (128 kbps),
        // sample-rate index 0 (44100 Hz), no padding.
        var id3Header = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        return [.. id3Header, .. BuildMp3Frame(), .. BuildMp3Frame()];
    }

    private static byte[] BuildMp3Frame()
    {
        const int bitrateKbps = 128;
        const int sampleRateHz = 44100;
        var frame = new byte[144 * bitrateKbps * 1000 / sampleRateHz];
        frame[0] = 0xFF;
        frame[1] = 0xFB; // sync(3)=111, version(2)=11 (MPEG1), layer(2)=01 (Layer III), protection=1
        frame[2] = 9 << 4; // bitrate index 9, sample-rate index 0, no padding
        return frame;
    }

    private sealed class DeterministicCatalogClient(string? bookIdOnFirstCall) : ICwaCatalogClient
    {
        public string? NextBookId { get; set; } = bookIdOnFirstCall;

        public Task<BookMatchResult> FindBookIdAsync(
            string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken) =>
            Task.FromResult(NextBookId is null
                ? BookMatchResult.NoMatchResult
                : BookMatchResult.Match(new CandidateBook(NextBookId, title, author)));
    }

    /// <summary>Stands in for the real CWA e-reader session -- no live CWA is reachable from this test.</summary>
    private sealed class DeterministicEbookDeliveryProvider : IEbookDeliveryProvider
    {
        public string Id => "cwa";

        public Task<bool> CanDeliverAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<EbookDeliveryOutcome> DeliverAsync(
            string providerBookId, string bookFormat, bool convert, string recipientEmail,
            CancellationToken cancellationToken) =>
            Task.FromResult(EbookDeliveryOutcome.Delivered("Sent."));

        public Task<ConnectionTestOutcome> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestOutcome(true, "ok"));
    }
}
