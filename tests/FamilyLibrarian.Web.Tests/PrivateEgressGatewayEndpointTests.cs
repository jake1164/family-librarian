using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Covers the private-egress gateway admin settings surface.</summary>
[TestClass]
public sealed class PrivateEgressGatewayEndpointTests
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

    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    [TestMethod]
    public async Task TheEndpointCanBeSavedAndReadBack()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/private-egress-gateway/endpoint",
            new SetPrivateEgressGatewayEndpointRequest("http://gluetun:8888"));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var read = await client.GetFromJsonAsync<PrivateEgressGatewayResponse>("/api/v1/admin/private-egress-gateway/");
        Assert.IsNotNull(read);
        Assert.AreEqual("http://gluetun:8888", read.GatewayEndpoint);
    }

    [TestMethod]
    public async Task AnInvalidEndpointIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/private-egress-gateway/endpoint",
            new SetPrivateEgressGatewayEndpointRequest("not-a-url"));

        Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
    }

    [TestMethod]
    public async Task TestingAnUnreachableEndpointReportsFailure()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        await client.PutAsJsonAsync(
            "/api/v1/admin/private-egress-gateway/endpoint",
            new SetPrivateEgressGatewayEndpointRequest("http://127.0.0.1:1"));

        var test = await client.PostAsync("/api/v1/admin/private-egress-gateway/test", content: null);
        Assert.AreEqual(HttpStatusCode.OK, test.StatusCode);
        var result = await test.Content.ReadFromJsonAsync<PrivateEgressGatewayResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(false, result.LastTestSucceeded);
    }

    [TestMethod]
    public async Task ANonAdminIsForbiddenOnTheGatewayRoutes()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var read = await client.GetAsync("/api/v1/admin/private-egress-gateway/");
        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/private-egress-gateway/enabled", new SetPrivateEgressGatewayEnabledRequest(true));

        Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
