using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Proves the real endpoint wiring for the format-readiness gate end to end:
/// request creation is refused for an unready format, and the catalog surfaces
/// the same signal so the UI need not guess. Every test here swaps in a fake
/// <see cref="IFormatReadinessService"/> — the ordinary test suite defaults to
/// <see cref="AlwaysReadyFormatReadinessService"/> so this is the only place
/// exercising an unready result.
/// </summary>
[TestClass]
public sealed class FormatReadinessEndpointTests
{
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
    public async Task RequestCreationIsRefusedForAFormatThatIsNotReady()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, notReadyMediaType: RequestMediaType.Ebook);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var workId = await ResolveTheHobbitAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Ebook"], null, true, false));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "CWA is unreachable in this test.");
    }

    [TestMethod]
    public async Task RequestCreationForAReadyFormatIsUnaffectedByAnotherFormatsOutage()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, notReadyMediaType: RequestMediaType.Ebook);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var workId = await ResolveTheHobbitAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(workId, ["Audiobook"], null, true, false));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    [TestMethod]
    public async Task TheCatalogFulfillmentOptionsResponseSurfacesTheReadinessReason()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, notReadyMediaType: RequestMediaType.Ebook);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var workId = await ResolveTheHobbitAsync(client);

        var response = await client.GetAsync($"/api/v1/catalog/works/{workId}/fulfillment-options");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<WorkFulfillmentOptionsResponse>();
        Assert.IsNotNull(options);
        Assert.IsNotNull(options.EbookReadiness);
        Assert.IsFalse(options.EbookReadiness!.IsReady);
        StringAssert.Contains(options.EbookReadiness.Reason, "CWA is unreachable in this test.");
        Assert.IsNotNull(options.AudiobookReadiness);
        Assert.IsTrue(options.AudiobookReadiness!.IsReady);
    }

    private static FamilyLibrarianAppFactory CreateFactory(WebTestFixture fixture, RequestMediaType notReadyMediaType) =>
        new(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IFormatReadinessService>();
                services.AddSingleton<IFormatReadinessService>(new FakeFormatReadinessService(notReadyMediaType));
            });

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

    private static async Task<Guid> ResolveTheHobbitAsync(HttpClient client)
    {
        var resolve = await client.PostAsync(
            "/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        return work.Id;
    }

    /// <summary>Reports one given media type as not ready; every other type is ready.</summary>
    private sealed class FakeFormatReadinessService(RequestMediaType notReadyMediaType) : IFormatReadinessService
    {
        public Task<FormatReadiness> CheckAsync(RequestMediaType mediaType, CancellationToken cancellationToken) =>
            Task.FromResult(mediaType == notReadyMediaType
                ? FormatReadiness.NotReady("CWA is unreachable in this test.")
                : FormatReadiness.Ready);
    }
}
