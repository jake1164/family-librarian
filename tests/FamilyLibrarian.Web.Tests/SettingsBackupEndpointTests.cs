using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Contracts.SettingsBackups;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Exercises the portable settings archive through the authenticated endpoint
/// boundary, including Data Protection ciphertext validation on import.
/// </summary>
[TestClass]
public sealed class SettingsBackupEndpointTests
{
    private const string Passphrase = "settings-backup-passphrase-8675309";
    private const string SftpPrivateKey = "test-sftp-key-do-not-leak-8675309";
    private const string SmtpPassword = "test-smtp-password-do-not-leak-8675309";

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
    public async Task AnEncryptedArchiveCanPreviewAndSeedACleanMatchingInstance()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await CreateAdminClientWithTokenAsync(fixture);

        var settings = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest("Local", "/data/cwa-ingest", null, null, null, null, "PrivateKey", null, null));
        Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);

        var secret = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/sftp-key", new SetPublishingSecretRequest(SftpPrivateKey));
        Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode);

        var smtpSettings = await client.PutAsJsonAsync(
            "/api/v1/admin/communications/smtp/",
            new SetSmtpSettingsRequest("smtp.example.test", 587, "StartTls", "mailer", "library@example.test", "Family Librarian"));
        Assert.AreEqual(HttpStatusCode.OK, smtpSettings.StatusCode);
        var smtpPassword = await client.PutAsJsonAsync(
            "/api/v1/admin/communications/smtp/password", new SetSmtpPasswordRequest(SmtpPassword));
        Assert.AreEqual(HttpStatusCode.OK, smtpPassword.StatusCode);

        var export = await client.PostAsJsonAsync(
            "/api/v1/admin/settings-backup/", new CreateSettingsBackupRequest(Passphrase));
        Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
        var archive = await export.Content.ReadAsByteArrayAsync();
        Assert.IsFalse(
            System.Text.Encoding.UTF8.GetString(archive).Contains(SftpPrivateKey, StringComparison.Ordinal),
            "An archive must not expose a stored credential as plaintext.");
        Assert.IsFalse(
            System.Text.Encoding.UTF8.GetString(archive).Contains(SmtpPassword, StringComparison.Ordinal),
            "An archive must not expose a stored SMTP password as plaintext.");

        await ClearSettingsAsync(fixture);

        var tampered = archive.ToArray();
        tampered[^1] ^= 0x01;
        using (var tamperedForm = CreateArchiveForm(tampered))
        using (var malformedPreview = await client.PostAsync(
            "/api/v1/admin/settings-backup/preview", tamperedForm))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, malformedPreview.StatusCode);
        }

        using var previewForm = CreateArchiveForm(archive);
        using var previewResponse = await client.PostAsync(
            "/api/v1/admin/settings-backup/preview", previewForm);
        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<SettingsBackupPreviewResponse>();
        Assert.IsNotNull(preview);
        Assert.IsTrue(preview.CanImport);
        Assert.AreEqual(1, preview.Counts.CwaSettings);
        Assert.AreEqual(1, preview.Counts.SmtpSettings);

        using var importForm = CreateArchiveForm(archive);
        using var importResponse = await client.PostAsync("/api/v1/admin/settings-backup/import", importForm);
        Assert.AreEqual(HttpStatusCode.OK, importResponse.StatusCode);
        var imported = await importResponse.Content.ReadFromJsonAsync<SettingsBackupImportResponse>();
        Assert.IsNotNull(imported);
        Assert.AreEqual(1, imported.Counts.CwaSettings);
        Assert.AreEqual(1, imported.Counts.SmtpSettings);

        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
        var restored = await database.CwaSettings.AsNoTracking().SingleAsync();
        Assert.IsNotNull(restored.ProtectedSftpPrivateKey);
        var restoredSecret = protector.Unprotect(
            PublishingSecretPurposes.CwaSftpPrivateKey,
            restored.ProtectedSftpPrivateKey,
            restored.SftpPrivateKeyFormatVersion);
        Assert.AreEqual(SftpPrivateKey, restoredSecret);

        var restoredSmtp = await database.SmtpSettings.AsNoTracking().SingleAsync();
        Assert.IsNotNull(restoredSmtp.ProtectedPassword);
        var restoredSmtpPassword = protector.Unprotect(
            CommunicationSecretPurposes.SmtpPassword,
            restoredSmtp.ProtectedPassword,
            restoredSmtp.PasswordFormatVersion);
        Assert.AreEqual(SmtpPassword, restoredSmtpPassword);
    }

    private static async Task<HttpClient> CreateAdminClientWithTokenAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static MultipartFormDataContent CreateArchiveForm(byte[] archive)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(Passphrase), "passphrase");
        form.Add(new ByteArrayContent(archive), "archive", "settings.family-librarian-settings");
        return form;
    }

    private static async Task ClearSettingsAsync(WebTestFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.CwaSettings.ExecuteDeleteAsync();
        await database.SmtpSettings.ExecuteDeleteAsync();
    }
}
