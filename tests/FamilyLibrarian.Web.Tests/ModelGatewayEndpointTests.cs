using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M4-0 over the real host: admin-only, off by default, key write-only, no route that calls a model.</summary>
[TestClass]
public sealed class ModelGatewayEndpointTests
{
    private const string BasePath = "/api/v1/admin/model-gateway";
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password, bool token = true)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (token) http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    [TestMethod]
    public async Task AnonymousIsChallengedAndAMemberIsForbiddenOnEveryRoute()
    {
        await using var factory = Factory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{BasePath}/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync($"{BasePath}/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, []))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/enabled", new SetModelGatewayEnabledRequest(true))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/api-key", new SetModelGatewayApiKeyRequest("sk-member"))).StatusCode);
    }

    [TestMethod]
    public async Task AMutationWithoutAnAntiforgeryTokenIsRejected()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword, token: false);

        var response = await admin.PutAsJsonAsync($"{BasePath}/enabled", new SetModelGatewayEnabledRequest(true));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task EverythingIsOffByDefaultAndTheKeyIsNeverReturned()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        var initial = await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/");
        Assert.IsFalse(initial!.IsEnabled);
        Assert.IsFalse(initial.AllowCloud);
        Assert.IsFalse(initial.HasApiKey);
        Assert.AreEqual(5, initial.Roles.Count);
        Assert.IsTrue(initial.Roles.All(role => !role.IsEnabled));

        using var stored = await admin.PutAsJsonAsync($"{BasePath}/api-key", new SetModelGatewayApiKeyRequest("sk-super-secret-value"));
        var body = await stored.Content.ReadAsStringAsync();
        Assert.IsTrue(stored.IsSuccessStatusCode, body);
        Assert.IsFalse(body.Contains("sk-super-secret-value", StringComparison.Ordinal));
        var reread = await admin.GetStringAsync($"{BasePath}/");
        Assert.IsFalse(reread.Contains("sk-super-secret-value", StringComparison.Ordinal));
        Assert.IsTrue((await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/"))!.HasApiKey);
    }

    [TestMethod]
    public async Task ConfiguringRolesRoundTripsAndCloudNeedsExplicitOptIn()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var cloudRole = new ModelRoleDto("ArtifactValidationAssessment", true, "Cloud", null, "cloud/alias");

        using var refused = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest("http://litellm.example.test:4000", false, [cloudRole]));
        using var unknown = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, [new ModelRoleDto("Nonsense", true, "Local", "a", null)]));
        using var saved = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(
            "http://litellm.example.test:4000", true, [cloudRole, new ModelRoleDto("CandidateAmbiguityAssessment", true, "Local", "local/alias", null)]));

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var settings = (await saved.Content.ReadFromJsonAsync<ModelGatewaySettingsResponse>())!;
        Assert.IsTrue(settings.AllowCloud);
        Assert.IsFalse(settings.IsEnabled, "Saving never turns the master switch on.");
        Assert.AreEqual("local/alias", settings.Roles.Single(role => role.Role == "CandidateAmbiguityAssessment").LocalModel);
        Assert.AreEqual("Cloud", settings.Roles.Single(role => role.Role == "ArtifactValidationAssessment").Placement);
    }
}
