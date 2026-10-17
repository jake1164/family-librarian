using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A new request wakes the background fulfillment worker instead of waiting out its two-minute sweep. The signal is only
/// a latency hint, so these pin the two things that keep it honest: it fires only for a request that really was created,
/// and only after that request is committed (otherwise the worker could wake, find nothing, and sleep again).
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
        signal.CountCommittedRequests = () => CountRequests(factory);
        using var admin = await LoginAsync(factory);
        var work = await ResolveAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(1, signal.Calls, "A new request should wake the worker exactly once.");
        Assert.AreEqual(1, signal.CommittedRequestsAtSignal, "The worker must be woken only once the request is visible to it.");
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

    private static int CountRequests(FamilyLibrarianAppFactory factory)
    {
        // A separate scope and connection: it sees only what has been committed.
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().BookRequests.AsNoTracking().Count();
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

        public int CommittedRequestsAtSignal { get; private set; }

        public Func<int>? CountCommittedRequests { get; set; }

        public void Request()
        {
            Calls++;
            CommittedRequestsAtSignal = CountCommittedRequests?.Invoke() ?? -1;
        }
    }
}
