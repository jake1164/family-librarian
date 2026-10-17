using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A request that needs searching wakes the background fulfillment worker instead of waiting out its two-minute sweep:
/// a new request, a requester asking again after withdrawing, an administrator returning a request to the queue, and an
/// administrator's bulk recheck. The signal is only a latency hint, so these pin the two things that keep it honest: it
/// fires only when a request really was queued, and only after that is committed (otherwise the worker could wake, find
/// nothing waiting, and sleep again).
/// </summary>
[TestClass]
public sealed class RequestCreationSignalTests
{
    [TestMethod]
    public async Task CreatingARequestWakesTheWorkerAfterTheRequestIsCommitted()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var signal = new RecordingSignal();
        await using var factory = CreateFactory(fixture, signal);
        signal.CountCommittedPending = () => CountPending(factory);
        using var admin = await LoginAsync(factory);
        var work = await ResolveAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(1, signal.Calls, "A new request should wake the worker exactly once.");
        Assert.AreEqual(1, signal.CommittedPendingAtSignal, "The worker must be woken only once the request is visible to it.");
    }

    [TestMethod]
    public async Task AskingAgainAfterWithdrawingWakesTheWorkerAfterTheRequestIsQueuedAgain()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var signal = new RecordingSignal();
        await using var factory = CreateFactory(fixture, signal);
        signal.CountCommittedPending = () => CountPending(factory);
        using var admin = await LoginAsync(factory);
        var request = await CreateRequestAsync(admin);
        Assert.AreEqual(1, signal.Calls);

        (await admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/transitions", new ChangeBookRequestStatusRequest("Cancelled", null)))
            .EnsureSuccessStatusCode();
        Assert.AreEqual(1, signal.Calls, "Withdrawing leaves nothing to search, so it must not wake the worker.");

        (await admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/transitions", new ChangeBookRequestStatusRequest("PendingAcquisition", null)))
            .EnsureSuccessStatusCode();

        Assert.AreEqual(2, signal.Calls);
        Assert.AreEqual(1, signal.CommittedPendingAtSignal, "The request must already be queued when the worker is woken.");
    }

    [TestMethod]
    public async Task AnAdministratorReturningARequestToTheQueueWakesTheWorker()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var signal = new RecordingSignal();
        await using var factory = CreateFactory(fixture, signal);
        signal.CountCommittedPending = () => CountPending(factory);
        using var admin = await LoginAsync(factory);
        var request = await CreateRequestAsync(admin);
        var toReview = await TransitionAsync(admin, request.Id, "NeedsReview", request.Version);
        Assert.AreEqual(1, signal.Calls, "Moving a request to review leaves nothing to search.");

        await TransitionAsync(admin, request.Id, "PendingAcquisition", toReview.Version);

        Assert.AreEqual(2, signal.Calls);
        Assert.AreEqual(1, signal.CommittedPendingAtSignal);
    }

    [TestMethod]
    public async Task ABulkRecheckWakesTheWorkerOnlyWhenItQueuedSomething()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var signal = new RecordingSignal();
        await using var factory = CreateFactory(fixture, signal);
        signal.CountCommittedPending = () => CountPending(factory);
        using var admin = await LoginAsync(factory);

        (await admin.PostAsJsonAsync("/api/v1/admin/requests/recheck", new { providerId = (string?)null })).EnsureSuccessStatusCode();
        Assert.AreEqual(0, signal.Calls, "A recheck with nothing in review has nothing to search.");

        var request = await CreateRequestAsync(admin);
        await TransitionAsync(admin, request.Id, "NeedsReview", request.Version);
        var before = signal.Calls;

        (await admin.PostAsJsonAsync("/api/v1/admin/requests/recheck", new { providerId = (string?)null })).EnsureSuccessStatusCode();

        Assert.AreEqual(before + 1, signal.Calls);
        Assert.AreEqual(1, signal.CommittedPendingAtSignal);
    }

    [TestMethod]
    public async Task ARequestThatIsRefusedDoesNotWakeTheWorker()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var signal = new RecordingSignal();
        await using var factory = CreateFactory(fixture, signal);
        using var admin = await LoginAsync(factory);
        var work = await ResolveAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, [], null, false, false));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, signal.Calls);
    }

    private static FamilyLibrarianAppFactory CreateFactory(WebTestFixture fixture, RecordingSignal signal) =>
        new(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IAutomaticFulfillmentSignal>();
            services.AddSingleton<IAutomaticFulfillmentSignal>(signal);
        });

    private static int CountPending(FamilyLibrarianAppFactory factory)
    {
        // A separate scope and connection: it sees only what has been committed.
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().BookRequests.AsNoTracking()
            .Count(request => request.Status == RequestStatus.PendingAcquisition);
    }

    private static async Task<BookRequestResponse> CreateRequestAsync(HttpClient admin)
    {
        var work = await ResolveAsync(admin);
        var response = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookRequestResponse>())!;
    }

    private static async Task<BookRequestResponse> TransitionAsync(HttpClient admin, Guid requestId, string status, uint version)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/admin/requests/{requestId}/transitions", new ChangeBookRequestStatusRequest(status, "test", version));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdminBookRequestResponse>())!.Request;
    }

    private static async Task<HttpClient> LoginAsync(FamilyLibrarianAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static async Task<CatalogWorkResponse> ResolveAsync(HttpClient admin)
    {
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        return work;
    }

    private sealed class RecordingSignal : IAutomaticFulfillmentSignal
    {
        public int Calls { get; private set; }

        /// <summary>How many requests were already queued (committed) the moment the worker was last woken.</summary>
        public int CommittedPendingAtSignal { get; private set; }

        public Func<int>? CountCommittedPending { get; set; }

        public void Request()
        {
            Calls++;
            CommittedPendingAtSignal = CountCommittedPending?.Invoke() ?? -1;
        }
    }
}
