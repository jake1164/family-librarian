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

    // --- alpha.6 M1-5a: provenance comes from the authenticated context, never from the caller ----------------

    [TestMethod]
    public async Task AnAdministratorsUploadRecordsTheAuthenticatedAdministratorAsItsImmutableOrigin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>();

        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.AsNoTracking().SingleAsync(a => a.Id == result!.MediaAssetId);
        var adminId = await database.Users.Where(user => user.Email == FamilyLibrarianAppFactory.AdminEmail).Select(user => user.Id).SingleAsync();
        Assert.AreEqual(AssetOrigin.ManualUpload, asset.Origin);
        Assert.AreEqual(adminId, asset.OriginUserId, "The origin user is the authenticated administrator.");
        Assert.IsTrue(asset.IdentityPreConfirmed, "Existing behavior preserved.");
        var attestation = await database.ArtifactEvidenceItems.AsNoTracking()
            .SingleAsync(item => item.MediaAssetId == asset.Id && item.Dimension == FamilyLibrarian.Domain.Evidence.EvidenceDimension.IdentityAttestation);
        Assert.AreEqual("artifact.identity_attestation.manual_upload", attestation.Code);
    }

    [TestMethod]
    public async Task AnOrdinaryUsersUploadIsRefusedAndCreatesNothingEvenWhenItClaimsAPrivilegedOrigin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var owner = await CreateTokenClientAsync(fixture, isAdmin: false);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, owner);
        owner.DefaultRequestHeaders.Add("X-Asset-Origin", "ManualUpload");
        owner.DefaultRequestHeaders.Add("X-Origin-User-Id", Guid.NewGuid().ToString());
        var form = BuildUpload(BuildMinimalEpubBytes(), "book.epub");
        form.Add(new StringContent("ManualUpload"), "origin");
        form.Add(new StringContent("true"), "identityPreConfirmed");

        var response = await owner.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import?origin=ManualUpload", form);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "Authorization decides, not claims in the request.");
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(0, await database.MediaAssets.CountAsync(a => a.AssociatedRequestFormatId == formatId));
    }

    [TestMethod]
    public async Task ClaimsSuppliedByAnAdministratorInTheRequestAreIgnoredWhenRecordingProvenance()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, admin);
        var someoneElse = Guid.NewGuid();
        admin.DefaultRequestHeaders.Add("X-Asset-Origin", "BuiltInProvider");
        admin.DefaultRequestHeaders.Add("X-Origin-User-Id", someoneElse.ToString());
        var form = BuildUpload(BuildMinimalEpubBytes(), "book.epub");
        form.Add(new StringContent("BuiltInProvider"), "origin");
        form.Add(new StringContent(someoneElse.ToString()), "originUserId");
        form.Add(new StringContent("false"), "identityPreConfirmed");

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import?origin=Legacy&originUserId={someoneElse}", form);

        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var assets = await database.MediaAssets.AsNoTracking().Where(a => a.AssociatedRequestFormatId == formatId).ToListAsync();
        var adminId = await database.Users.Where(user => user.Email == FamilyLibrarianAppFactory.AdminEmail).Select(user => user.Id).SingleAsync();
        // Whether the endpoint ignores or rejects the extra input, no asset may carry a claimed identity or origin.
        Assert.IsTrue(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, response.StatusCode.ToString());
        Assert.IsTrue(assets.All(a => a.Origin == AssetOrigin.ManualUpload && a.OriginUserId == adminId && a.IdentityPreConfirmed));
        Assert.IsFalse(assets.Any(a => a.OriginUserId == someoneElse));
    }

    [TestMethod]
    public async Task TheDatabaseRefusesToChangeAnAssetsOriginOrOriginUserButStillAllowsOrdinaryUpdates()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, admin);
        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>();

        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = result!.MediaAssetId;

        var origin = await Assert.ThrowsAsync<Exception>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE acquisition.media_assets SET origin = {"BuiltInProvider"} WHERE id = {id}"));
        StringAssert.Contains(origin.ToString(), "immutable");
        var user = await Assert.ThrowsAsync<Exception>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE acquisition.media_assets SET origin_user_id = NULL WHERE id = {id}"));
        StringAssert.Contains(user.ToString(), "immutable");

        var allowed = await database.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE acquisition.media_assets SET identity_hold_code = {"identity.hold.content_inconclusive"} WHERE id = {id}");
        Assert.AreEqual(1, allowed, "Only the origin is protected; other columns update as before.");
        database.ChangeTracker.Clear();
        Assert.AreEqual(AssetOrigin.ManualUpload, (await database.MediaAssets.AsNoTracking().SingleAsync(a => a.Id == id)).Origin);
    }

    [TestMethod]
    public async Task AnIdentityHoldIsLabeledWithItsCodeAndTheCodeClearsWhenAnAdministratorOverridesIt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateTokenClientAsync(fixture);
        var (requestId, formatId) = await CreateEbookRequestAsync(fixture, admin);
        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(EpubTestFixture.BuildMinimalEpubBytes("A Different Book", "Someone Else"), "book.epub"));
        var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        var id = result!.MediaAssetId;

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var held = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.AsNoTracking().SingleAsync(a => a.Id == id);
            Assert.AreEqual(MediaAssetStorageState.Unmatched, held.StorageState);
            Assert.AreEqual("identity.hold.content_contradicted", held.IdentityHoldCode);
            Assert.IsNotNull(held.IdentityMismatchReason, "The existing human-readable reason is unchanged.");
        }

        var overridden = await admin.PostAsJsonAsync(
            $"/api/v1/admin/media-assets/{id}/override-identity", new { reason = "Checked by hand; this is the right book." });
        Assert.IsTrue(overridden.IsSuccessStatusCode, overridden.StatusCode.ToString());

        await using var after = fixture.Services.CreateAsyncScope();
        var released = await after.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.IsNull(released.IdentityHoldCode, "A stale hold code must never outlive the hold.");
        Assert.IsNull(released.IdentityMismatchReason);
        Assert.AreEqual(AssetOrigin.ManualUpload, released.Origin);
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
            new CreateBookRequestRequest(await WebTestFixture.Require(_fixture).CopyWorkForTestAsync(work.Id), ["Ebook"], null, false, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == "Ebook");
        return (request.Id, format.FormatId);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();
}
