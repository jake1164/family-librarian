using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Contracts.ModelTesting;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The routes behind switches that save on their own: administrator-only, anti-forgery protected, and changing only the
/// switch they name.
/// </summary>
[TestClass]
public sealed class SettingSwitchEndpointTests
{
    private const string Gateway = "/api/v1/admin/model-gateway";
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null)
        {
            await fixture.DisposeAsync();
        }
    }

    private static (string Path, object Body)[] SwitchRoutes() =>
    [
        ($"{Gateway}/roles/CandidateTriage/enabled", new SetModelRoleEnabledRequest(true)),
        ($"{Gateway}/permissions", new SetModelGatewayPermissionsRequest(AllowCloud: true)),
        ($"{Gateway}/capture/switches", new SetDecisionCaptureSwitchesRequest(ChatCaptureEnabled: true)),
        ("/api/v1/admin/model-test/allow-cloud", new SetModelTestCloudRequest(true))
    ];

    [TestMethod]
    public async Task EverySwitchRouteIsAdministratorOnly()
    {
        using var member = await WithTokenAsync(await WebTestFixture.Require(fixture).CreateUserClientAsync());

        foreach (var (path, body) in SwitchRoutes())
        {
            var response = await member.PutAsJsonAsync(path, body);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, path);
        }
    }

    [TestMethod]
    public async Task EverySwitchRouteRequiresAnAntiforgeryToken()
    {
        using var admin = await WebTestFixture.Require(fixture).CreateAdminClientAsync();

        foreach (var (path, body) in SwitchRoutes())
        {
            var response = await admin.PutAsJsonAsync(path, body);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, path);
        }
    }

    [TestMethod]
    public async Task AnUnknownRoleAndAnEmptyChangeAreRefused()
    {
        using var admin = await AdminAsync();

        var unknown = await admin.PutAsJsonAsync($"{Gateway}/roles/NotARole/enabled", new SetModelRoleEnabledRequest(true));
        var emptyPermissions = await admin.PutAsJsonAsync($"{Gateway}/permissions", new SetModelGatewayPermissionsRequest());
        var emptySwitches = await admin.PutAsJsonAsync($"{Gateway}/capture/switches", new SetDecisionCaptureSwitchesRequest());

        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, emptyPermissions.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, emptySwitches.StatusCode);
    }

    [TestMethod]
    public async Task ACaptureSwitchChangesOnlyItself()
    {
        using var admin = await AdminAsync();
        var before = await admin.GetFromJsonAsync<DecisionCaptureSettingsResponse>($"{Gateway}/capture/");

        var response = await admin.PutAsJsonAsync(
            $"{Gateway}/capture/switches", new SetDecisionCaptureSwitchesRequest(ChatCaptureEnabled: !before!.ChatCaptureEnabled));

        var after = await response.Content.ReadFromJsonAsync<DecisionCaptureSettingsResponse>();
        Assert.AreEqual(!before.ChatCaptureEnabled, after!.ChatCaptureEnabled);
        Assert.AreEqual(before.DecisionCaptureEnabled, after.DecisionCaptureEnabled);
        Assert.AreEqual(before.Limits, after.Limits);
    }

    [TestMethod]
    public async Task TheModelTestCloudSwitchChangesOnlyItself()
    {
        using var admin = await AdminAsync();
        var before = await admin.GetFromJsonAsync<ModelTestSettingsResponse>("/api/v1/admin/model-test/");

        var response = await admin.PutAsJsonAsync("/api/v1/admin/model-test/allow-cloud", new SetModelTestCloudRequest(true));

        var after = await response.Content.ReadFromJsonAsync<ModelTestSettingsResponse>();
        Assert.AreEqual(before! with { AllowCloud = true }, after);
    }

    [TestMethod]
    public async Task TheAudiobookshelfTestRouteTestsWhatItIsSentWithoutRecordingUnsavedValues()
    {
        using var admin = await AdminAsync();
        await admin.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/",
            new SetAudiobookshelfSettingsRequest("https://saved.abs.example.test", null, "library-1", "folder-1"));
        await admin.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/api-token", new SetPublishingSecretRequest("a-test-token"));

        var response = await admin.PostAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/test",
            new TestAudiobookshelfRequest("https://typed.abs.example.test", "library-1", "folder-1", null));

        var result = await response.Content.ReadFromJsonAsync<PublishingConnectionTestResponse>();
        StringAssert.Contains(result!.Message, "not saved yet");
        var settings = await admin.GetFromJsonAsync<AudiobookshelfSettingsResponse>("/api/v1/admin/publishing/audiobookshelf/");
        Assert.IsNull(settings!.LastTestSucceeded);
    }

    [TestMethod]
    public async Task AnEmptyAudiobookshelfTestRequestStillTestsAndRecordsTheSavedSettings()
    {
        // Existing automation sends {} to mean "test what is saved".
        using var admin = await AdminAsync();
        await admin.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/",
            new SetAudiobookshelfSettingsRequest("https://saved.abs.example.test", null, "library-1", "folder-1"));
        await admin.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/api-token", new SetPublishingSecretRequest("a-test-token"));

        var response = await admin.PostAsJsonAsync("/api/v1/admin/publishing/audiobookshelf/test", new { });

        var result = await response.Content.ReadFromJsonAsync<PublishingConnectionTestResponse>();
        Assert.IsTrue(result!.Succeeded, result.Message);
        var settings = await admin.GetFromJsonAsync<AudiobookshelfSettingsResponse>("/api/v1/admin/publishing/audiobookshelf/");
        Assert.AreEqual(true, settings!.LastTestSucceeded);
    }

    private static async Task<HttpClient> AdminAsync() =>
        await WithTokenAsync(await WebTestFixture.Require(fixture).CreateAdminClientAsync());

    private static async Task<HttpClient> WithTokenAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }
}
