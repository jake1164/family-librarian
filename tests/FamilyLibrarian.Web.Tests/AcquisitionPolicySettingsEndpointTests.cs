using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Policy;
using FamilyLibrarian.Domain.Policy;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>Covers the admin-facing acquisition-policy settings surface.</summary>
[TestClass]
public sealed class AcquisitionPolicySettingsEndpointTests
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
    public async Task AnAdminCanListTheFourFixedProfiles()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var profiles = await client.GetFromJsonAsync<PolicyProfileResponse[]>("/api/v1/admin/policy/profiles");

        Assert.IsNotNull(profiles);
        CollectionAssert.AreEquivalent(
            new[]
            {
                PolicyProfileIds.ManualChoice, PolicyProfileIds.LibraryFirst,
                PolicyProfileIds.FreeFirst, PolicyProfileIds.LowestCost
            },
            profiles.Select(profile => profile.Id).ToArray());
    }

    [TestMethod]
    [DataRow(PolicyProfileIds.LibraryFirst)]
    [DataRow(PolicyProfileIds.FreeFirst)]
    [DataRow(PolicyProfileIds.LowestCost)]
    public async Task AnAdminCanSetTheDefaultToEachKnownProfile(string profileId)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/policy/settings", new SetDefaultPolicyProfileRequest(profileId));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        var written = await write.Content.ReadFromJsonAsync<AcquisitionPolicySettingsResponse>();
        Assert.IsNotNull(written);
        Assert.AreEqual(profileId, written.DefaultProfileId);

        var read = await client.GetFromJsonAsync<AcquisitionPolicySettingsResponse>("/api/v1/admin/policy/settings");
        Assert.IsNotNull(read);
        Assert.AreEqual(profileId, read.DefaultProfileId);
    }

    [TestMethod]
    public async Task AnUnknownProfileIdIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/policy/settings", new SetDefaultPolicyProfileRequest("not-a-real-profile"));

        Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminIsForbiddenOnBothRoutes()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var read = await client.GetAsync("/api/v1/admin/policy/settings");
        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/policy/settings", new SetDefaultPolicyProfileRequest(PolicyProfileIds.LibraryFirst));

        Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
