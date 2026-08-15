using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the same promise the Admin Metadata Integrations surface makes,
/// applied to the two new publishing-destination settings: a stored secret is
/// write-only and never appears in any response body, and every mutation is
/// anti-forgery protected.
/// </summary>
[TestClass]
public sealed class PublishingSettingsEndpointTests
{
    private const string CwaSecretValue = "-----BEGIN OPENSSH PRIVATE KEY-----do-not-leak-8675309-----END-----";
    private const string AudiobookshelfSecretValue = "abs-live-token-do-not-leak-8675309";

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
    public async Task CwaSettingsCanBeSavedAndReadBack()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest("Local", "/data/cwa-ingest", null, null, null, null, null, null));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        var written = await write.Content.ReadFromJsonAsync<CwaSettingsResponse>();
        Assert.IsNotNull(written);
        Assert.AreEqual("Local", written.TransportMode);
        Assert.AreEqual("/data/cwa-ingest", written.LocalIngestPath);

        var read = await client.GetFromJsonAsync<CwaSettingsResponse>("/api/v1/admin/publishing/cwa/");
        Assert.IsNotNull(read);
        Assert.AreEqual("/data/cwa-ingest", read.LocalIngestPath);
    }

    [TestMethod]
    public async Task CwaSettingsWithSftpTransportButMissingFieldsIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest("Sftp", null, null, null, null, null, null, null));

        Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
    }

    [TestMethod]
    public async Task NoEndpointEverReturnsAStoredCwaSecret()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/sftp-key", new SetPublishingSecretRequest(CwaSecretValue));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var writeBody = await write.Content.ReadAsStringAsync();
        var getBody = await client.GetStringAsync("/api/v1/admin/publishing/cwa/");
        var testResponse = await client.PostAsync("/api/v1/admin/publishing/cwa/test", content: null);
        var testBody = await testResponse.Content.ReadAsStringAsync();

        foreach (var (name, body) in new[] { ("write", writeBody), ("get", getBody), ("test", testBody) })
        {
            StringAssert.DoesNotMatch(
                body,
                new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(CwaSecretValue)),
                $"The {name} response leaked the stored CWA secret.");
        }

        var settings = await client.GetFromJsonAsync<CwaSettingsResponse>("/api/v1/admin/publishing/cwa/");
        Assert.IsNotNull(settings);
        Assert.IsTrue(settings.HasSftpPrivateKey);
        Assert.IsNotNull(settings.SftpPrivateKeyHint);
    }

    [TestMethod]
    public async Task ClearingTheCwaSftpKeyRemovesIt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/sftp-key", new SetPublishingSecretRequest(CwaSecretValue));

        var clear = await client.DeleteAsync("/api/v1/admin/publishing/cwa/sftp-key");
        Assert.AreEqual(HttpStatusCode.OK, clear.StatusCode);

        var settings = await client.GetFromJsonAsync<CwaSettingsResponse>("/api/v1/admin/publishing/cwa/");
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.HasSftpPrivateKey);
    }

    [TestMethod]
    public async Task AudiobookshelfSettingsCanBeSavedAndReadBack()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/",
            new SetAudiobookshelfSettingsRequest("https://abs.example.test", "lib-1", "folder-1"));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var read = await client.GetFromJsonAsync<AudiobookshelfSettingsResponse>(
            "/api/v1/admin/publishing/audiobookshelf/");
        Assert.IsNotNull(read);
        Assert.AreEqual("https://abs.example.test", read.BaseUrl);
        Assert.AreEqual("lib-1", read.LibraryId);
        Assert.AreEqual("folder-1", read.FolderId);
    }

    [TestMethod]
    public async Task NoEndpointEverReturnsAStoredAudiobookshelfToken()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var write = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/api-token",
            new SetPublishingSecretRequest(AudiobookshelfSecretValue));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var writeBody = await write.Content.ReadAsStringAsync();
        var getBody = await client.GetStringAsync("/api/v1/admin/publishing/audiobookshelf/");

        foreach (var (name, body) in new[] { ("write", writeBody), ("get", getBody) })
        {
            StringAssert.DoesNotMatch(
                body,
                new System.Text.RegularExpressions.Regex(
                    System.Text.RegularExpressions.Regex.Escape(AudiobookshelfSecretValue)),
                $"The {name} response leaked the stored Audiobookshelf token.");
        }

        var settings = await client.GetFromJsonAsync<AudiobookshelfSettingsResponse>(
            "/api/v1/admin/publishing/audiobookshelf/");
        Assert.IsNotNull(settings);
        Assert.IsTrue(settings.HasApiToken);
    }

    [TestMethod]
    public async Task MutatingCwaSettingsWithoutAnAntiforgeryTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();

        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest("Local", "/data/cwa-ingest", null, null, null, null, null, null));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
