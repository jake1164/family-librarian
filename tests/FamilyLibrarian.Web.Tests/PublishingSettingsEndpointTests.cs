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
    private const string CwaSftpPassword = "cwa-sftp-password-do-not-leak-8675309";
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
            new SetCwaSettingsRequest("Local", "/data/cwa-ingest", null, null, null, null, "PrivateKey", null, null));
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
            new SetCwaSettingsRequest("Sftp", null, null, null, null, null, "PrivateKey", null, null));

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
        var ingestTestResponse = await client.PostAsync(
            "/api/v1/admin/publishing/cwa/test-ingest", content: null);
        var ingestTestBody = await ingestTestResponse.Content.ReadAsStringAsync();
        var opdsTestResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/publishing/cwa/test-opds",
            new TestCwaOpdsRequest("http://127.0.0.1:9", null, null));
        var opdsTestBody = await opdsTestResponse.Content.ReadAsStringAsync();

        foreach (var (name, body) in new[]
        {
            ("write", writeBody),
            ("get", getBody),
            ("ingest test", ingestTestBody),
            ("OPDS test", opdsTestBody)
        })
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
    public async Task SftpPasswordIsWriteOnlyAndSupportsPasswordAuthentication()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var settingsWrite = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest(
                "Sftp", null, "sftp.example.test", 22, "cwa", "/ingest", "Password", null, null));
        Assert.AreEqual(HttpStatusCode.OK, settingsWrite.StatusCode);

        var passwordWrite = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/sftp-password", new SetPublishingSecretRequest(CwaSftpPassword));
        Assert.AreEqual(HttpStatusCode.OK, passwordWrite.StatusCode);

        var responseBody = await passwordWrite.Content.ReadAsStringAsync();
        var settingsBody = await client.GetStringAsync("/api/v1/admin/publishing/cwa/");
        Assert.IsFalse(responseBody.Contains(CwaSftpPassword, StringComparison.Ordinal));
        Assert.IsFalse(settingsBody.Contains(CwaSftpPassword, StringComparison.Ordinal));

        var settings = await client.GetFromJsonAsync<CwaSettingsResponse>("/api/v1/admin/publishing/cwa/");
        Assert.IsNotNull(settings);
        Assert.AreEqual("Password", settings.SftpAuthenticationMode);
        Assert.IsTrue(settings.HasSftpPassword);
        Assert.IsNull(settings.SftpPasswordHint);
    }

    [TestMethod]
    public async Task SftpCannotBeEnabledUntilTheServerHostKeyIsTrusted()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest(
                "Sftp", null, "sftp.example.test", 22, "cwa", "/ingest", "Password", null, null));
        await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/sftp-password", new SetPublishingSecretRequest(CwaSftpPassword));

        var enableBeforeTrust = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled", new SetPublishingEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.BadRequest, enableBeforeTrust.StatusCode);

        const string fingerprint = "SHA256:example-host-key-fingerprint";
        var trust = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/sftp-host-key", new TrustSftpHostKeyRequest(fingerprint));
        Assert.AreEqual(HttpStatusCode.OK, trust.StatusCode);

        var enableAfterTrust = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled", new SetPublishingEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, enableAfterTrust.StatusCode);

        var settings = await client.GetFromJsonAsync<CwaSettingsResponse>("/api/v1/admin/publishing/cwa/");
        Assert.IsNotNull(settings);
        Assert.AreEqual(fingerprint, settings.SftpHostKeyFingerprint);
        Assert.IsNotNull(settings.SftpHostKeyTrustedAtUtc);
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
            new SetCwaSettingsRequest("Local", "/data/cwa-ingest", null, null, null, null, "PrivateKey", null, null));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
