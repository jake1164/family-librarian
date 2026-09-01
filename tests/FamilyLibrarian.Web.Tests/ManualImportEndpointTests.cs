using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Exercises the manual-import endpoint through the real host, including its
/// hand-rolled multipart parsing (deliberately not <c>IFormFile</c> binding —
/// see the handler's comment in Program.cs).
/// </summary>
[TestClass]
public sealed class ManualImportEndpointTests
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
    public async Task AnAdminCanManuallyImportAValidFile()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(result);
        Assert.AreNotEqual(Guid.Empty, result.MediaAssetId);

        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(a => a.Id == result.MediaAssetId);

        // The fixture's title/author match the-hobbit's catalog metadata, so a
        // clean scan is approved by policy and published during the upload
        // itself — see SecurityGateEndpointTests.ACleanFileIsApprovedByPolicyAndBecomesTrusted.
        Assert.AreEqual(MediaAssetStorageState.Trusted, asset.StorageState);
        Assert.AreEqual(formatId, asset.AssociatedRequestFormatId);
    }

    [TestMethod]
    public async Task AFileWhoseContentDoesNotMatchItsExtensionIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(Encoding.UTF8.GetBytes("not an epub"), "book.epub"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminCannotManuallyImport()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, owner);

        var response = await owner.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AnUploadWithoutAnAntiforgeryTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var tokenClient = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, tokenClient);

        using var admin = await fixture.CreateAdminClientAsync();
        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private static async Task<HttpClient> CreateTokenClientAsync(WebTestFixture fixture, bool isAdmin = true)
    {
        var client = isAdmin
            ? await fixture.CreateAdminClientAsync()
            : await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static async Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(
        WebTestFixture fixture,
        HttpClient client)
    {
        var resolve = await client.PostAsync(
            "/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        // ConfirmDuplicate: true — several tests in this class request the same
        // demo Work as the same admin account, which the app's real duplicate
        // detection would otherwise (correctly) reject as a repeat request.
        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(work.Id, ["Ebook"], null, true, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == "Ebook");
        return (request.Id, format.FormatId);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();
}
