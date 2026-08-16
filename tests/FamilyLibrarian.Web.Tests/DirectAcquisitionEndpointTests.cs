using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Exercises the bundled free-ebook acquisition endpoint through the real
/// host, with a fake <see cref="IDirectAcquisitionProvider"/> standing in for
/// Gutendex so the test never depends on a real network call.
/// </summary>
[TestClass]
public sealed class DirectAcquisitionEndpointTests
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
    public async Task AnAdminCanAcquireAMatchedFreeEbook()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(result);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(a => a.Id == result.MediaAssetId);
        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
        Assert.AreEqual(formatId, asset.AssociatedRequestFormatId);

        var job = await database.AcquisitionJobs.SingleAsync(j => j.Id == result.AcquisitionJobId);
        Assert.AreEqual("gutendex", job.ProviderId);
    }

    [TestMethod]
    public async Task AStaleProviderResultIdIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: false));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/nonexistent",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AnUnknownProviderIdIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/not-a-real-provider/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminCannotAcquire()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new FakeProvider(matches: true));
        using var admin = await CreateTokenClientAsync(factory);
        var (requestId, formatId) = await CreateEbookRequestAsync(admin);

        using var user = await CreateTokenClientAsync(factory, isAdmin: false);
        var response = await user.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/gutendex/1234",
            content: null);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static FamilyLibrarianAppFactory CreateFactory(WebTestFixture fixture, IDirectAcquisitionProvider provider) =>
        new(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IDirectAcquisitionProvider>();
                services.AddSingleton(provider);
            });

    private static async Task<HttpClient> CreateTokenClientAsync(FamilyLibrarianAppFactory factory, bool isAdmin = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = isAdmin ? FamilyLibrarianAppFactory.AdminEmail : WebTestFixture.UserEmail,
                Password = isAdmin ? FamilyLibrarianAppFactory.AdminPassword : WebTestFixture.UserPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static async Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(HttpClient client)
    {
        var resolve = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(work.Id, ["Ebook"], null, true));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == "Ebook");
        return (request.Id, format.FormatId);
    }

    /// <summary>Mirrors <c>ManualImportEndpointTests.BuildMinimalEpubBytes</c>: a minimal, sniffable EPUB.</summary>
    private static byte[] BuildMinimalEpubBytes()
    {
        const string entryName = "mimetype";
        const string content = "application/epub+zip";
        var nameBytes = Encoding.ASCII.GetBytes(entryName);
        var contentBytes = Encoding.ASCII.GetBytes(content);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0x04034B50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write((uint)contentBytes.Length);
        writer.Write((uint)contentBytes.Length);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(nameBytes);
        writer.Write(contentBytes);

        return stream.ToArray();
    }

    /// <summary>Always reports one "1234" DirectAcquisition match (or none), and fetches a fake EPUB.</summary>
    private sealed class FakeProvider(bool matches) : IDirectAcquisitionProvider
    {
        public string Id => "gutendex";

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
        {
            if (!matches || mediaType != RequestMediaType.Ebook)
            {
                return Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);
            }

            IReadOnlyList<FulfillmentOption> options =
            [
                new FulfillmentOption(
                    ProviderId: Id,
                    ProviderResultId: "1234",
                    WorkId: workId,
                    EditionId: null,
                    MediaType: RequestMediaType.Ebook,
                    OptionKind: OptionKind.DirectAcquisition,
                    AcquisitionMethod: AcquisitionMethod.DirectDownload,
                    Format: "epub",
                    Language: null,
                    Quality: null,
                    Availability: null,
                    Cost: 0m,
                    Currency: null,
                    LicenseOrUsageStatus: "Public domain",
                    DrmStatus: null,
                    ExternalActionUri: null,
                    ProviderData: "https://example.test/book.epub")
            ];
            return Task.FromResult(options);
        }

        public Task<DirectAcquisitionFile> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
            Task.FromResult(new DirectAcquisitionFile(new MemoryStream(BuildMinimalEpubBytes()), "book.epub"));
    }
}
