using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class SmtpSettingsEndpointTests
{
    private const string Password = "smtp-password-do-not-leak-8675309";
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

    [TestMethod]
    public async Task AdminCanConfigureTestAndEnableSmtpWithoutSecretDisclosure()
    {
        using var client = await CreateAdminClientAsync(WebTestFixture.Require(fixture));

        var settingsWrite = await client.PutAsJsonAsync(
            "/api/v1/admin/communications/smtp/",
            new SetSmtpSettingsRequest(
                "smtp.example.test", 587, "StartTls", "mailer", "library@example.test", "Family Librarian"));
        Assert.AreEqual(HttpStatusCode.OK, settingsWrite.StatusCode);

        var passwordWrite = await client.PutAsJsonAsync(
            "/api/v1/admin/communications/smtp/password", new SetSmtpPasswordRequest(Password));
        Assert.AreEqual(HttpStatusCode.OK, passwordWrite.StatusCode);

        var settingsBody = await client.GetStringAsync("/api/v1/admin/communications/smtp/");
        var passwordBody = await passwordWrite.Content.ReadAsStringAsync();
        Assert.IsFalse(settingsBody.Contains(Password, StringComparison.Ordinal));
        Assert.IsFalse(passwordBody.Contains(Password, StringComparison.Ordinal));

        var test = await client.PostAsJsonAsync(
            "/api/v1/admin/communications/smtp/test", new SendSmtpTestRequest("admin@example.test"));
        Assert.AreEqual(HttpStatusCode.OK, test.StatusCode);
        var testResponse = await test.Content.ReadFromJsonAsync<SmtpTestResponse>();
        Assert.IsNotNull(testResponse);
        Assert.IsTrue(testResponse.Succeeded);

        var enable = await client.PutAsJsonAsync(
            "/api/v1/admin/communications/smtp/enabled", new SetSmtpEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode);
        var status = await enable.Content.ReadFromJsonAsync<SmtpSettingsResponse>();
        Assert.IsNotNull(status);
        Assert.IsTrue(status.IsEnabled);
        Assert.IsTrue(status.HasPassword);
    }

    [TestMethod]
    public async Task MutatingSmtpSettingsWithoutAnAntiforgeryTokenIsRejected()
    {
        using var client = await WebTestFixture.Require(fixture).CreateAdminClientAsync();

        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/communications/smtp/",
            new SetSmtpSettingsRequest("smtp.example.test", 587, "StartTls", null, "library@example.test", null));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAdminClientAsync(WebTestFixture webFixture)
    {
        var client = await webFixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }
}
