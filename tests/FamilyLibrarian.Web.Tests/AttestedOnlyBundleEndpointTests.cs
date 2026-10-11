using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// M1-5b (P1) for a multi-file audiobook set: every track is verified and held, and a single administrator override
/// releases exactly the tracks held for the shared "no content check" reason.
/// </summary>
[TestClass]
public sealed class AttestedOnlyBundleEndpointTests
{
    private const int Tracks = 3;
    private const int Mp3Bitrate = 128;
    private const int Mp3SampleRate = 44100;

    private static byte[] Mp3(byte fill)
    {
        var frameLength = 144 * Mp3Bitrate * 1000 / Mp3SampleRate;
        var bytes = new byte[frameLength * 2];
        for (var frame = 0; frame < 2; frame++)
        {
            var offset = frame * frameLength;
            bytes[offset] = 0xFF;
            bytes[offset + 1] = 0xFB;
            bytes[offset + 2] = 9 << 4;
            Array.Fill(bytes, fill, offset + 4, frameLength - 4);
        }

        return bytes;
    }

    private sealed record Seeded(Guid[] AssetIds, Guid FormatId);

    private static async Task<(FamilyLibrarianAppFactory Factory, HttpClient Admin, Seeded Set)> SeedHeldSetAsync(
        WebTestFixture fixture, AssetOrigin origin)
    {
        var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, login.StatusCode);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(await fixture.CopyWorkForTestAsync(work.Id), ["Audiobook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        var formatId = request.Formats.Single(format => format.MediaType == "Audiobook").FormatId;

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAssetStagingStore>();
        var workId = (await database.BookRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).WorkId;

        var bundleId = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var track = 1; track <= Tracks; track++)
        {
            await using var content = new MemoryStream(Mp3((byte)(0x10 + track)));
            var staged = await store.WriteToQuarantineAsync(content, $"track{track}.mp3", 10_000_000, CancellationToken.None);
            var asset = new MediaAsset(
                workId, null, RequestMediaType.Audiobook, ".mp3", $"track{track}.mp3", staged.StoredFilename, staged.SizeBytes,
                staged.Sha256, staged.DetectedMimeType, formatId, null, DateTimeOffset.UtcNow,
                bundleId: bundleId, bundleSequence: track, bundleTrackCount: Tracks,
                identityPreConfirmed: true, origin: origin);
            database.MediaAssets.Add(asset);
            ids.Add(asset.Id);
        }

        await database.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<AutomatedSecurityPipeline>().EvaluateBundleAsync(ids, CancellationToken.None);
        return (factory, admin, new Seeded([.. ids], formatId));
    }

    private static async Task<List<MediaAsset>> LoadAsync(FamilyLibrarianAppFactory factory, Guid[] ids)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.MediaAssets.AsNoTracking().Where(asset => ids.Contains(asset.Id)).OrderBy(asset => asset.BundleSequence).ToListAsync();
    }

    [TestMethod]
    public async Task EveryTrackOfAnExternalSetWithNoContentCheckIsHeldAndOneOverrideReleasesTheWholeSet()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (factory, admin, set) = await SeedHeldSetAsync(fixture, AssetOrigin.ExternalProvider);
        await using var _ = factory;
        using var __ = admin;

        var held = await LoadAsync(factory, set.AssetIds);
        Assert.AreEqual(Tracks, held.Count);
        Assert.IsTrue(held.All(asset =>
                asset.StorageState == MediaAssetStorageState.Unmatched && asset.IdentityHoldCode == "identity.hold.attested_only_external"),
            "Every track is verified and held, not only the first: " + string.Join(", ", held.Select(asset => asset.StorageState)));

        var overridden = await admin.PostAsJsonAsync(
            $"/api/v1/admin/media-assets/{set.AssetIds[0]}/override-identity", new { reason = "Confirmed by listening to the set." });
        Assert.IsTrue(overridden.IsSuccessStatusCode, overridden.StatusCode.ToString());

        var released = await LoadAsync(factory, set.AssetIds);
        Assert.IsTrue(released.All(asset => asset.StorageState == MediaAssetStorageState.Trusted && asset.IdentityHoldCode is null),
            "One decision covers the tracks held for the same shared reason: " + string.Join(", ", released.Select(asset => asset.StorageState)));
    }

    [TestMethod]
    public async Task ATrackHeldForADifferentReasonIsNeverReleasedByTheSetOverride()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (factory, admin, set) = await SeedHeldSetAsync(fixture, AssetOrigin.ExternalProvider);
        await using var _ = factory;
        using var __ = admin;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var middle = set.AssetIds[1];
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE acquisition.media_assets SET identity_hold_code = {"identity.hold.content_contradicted"} WHERE id = {middle}");
        }

        var overridden = await admin.PostAsJsonAsync(
            $"/api/v1/admin/media-assets/{set.AssetIds[0]}/override-identity", new { reason = "Set looks right." });
        Assert.IsTrue(overridden.IsSuccessStatusCode, overridden.StatusCode.ToString());

        var after = await LoadAsync(factory, set.AssetIds);
        Assert.AreEqual(MediaAssetStorageState.Trusted, after[0].StorageState);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, after[1].StorageState, "Its own content-based hold is not covered by the shared-reason override.");
        Assert.AreEqual("identity.hold.content_contradicted", after[1].IdentityHoldCode);
        Assert.AreEqual(MediaAssetStorageState.Trusted, after[2].StorageState);
    }

    [TestMethod]
    public async Task ASetFromABuiltInProviderWithNoContentCheckIsTrustedAsBefore()
    {
        const AssetOrigin origin = AssetOrigin.BuiltInProvider;
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (factory, admin, set) = await SeedHeldSetAsync(fixture, origin);
        await using var _ = factory;
        using var __ = admin;

        var assets = await LoadAsync(factory, set.AssetIds);
        Assert.IsTrue(assets.All(asset => asset.StorageState == MediaAssetStorageState.Trusted && asset.IdentityHoldCode is null),
            "Built-in LibriVox/Gutenberg-style sets keep today's behavior: " + string.Join(", ", assets.Select(asset => asset.StorageState)));
    }
}
