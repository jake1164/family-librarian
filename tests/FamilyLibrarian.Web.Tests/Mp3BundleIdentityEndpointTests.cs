using System.Net.Http.Json;
using System.Text;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Publishing;
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
/// AUTOFIND 3a for a multi-file audiobook set from an external provider: every track is read for its ID3 tags, a set whose
/// tags all match clears on content, and one German track holds the whole set.
/// </summary>
[TestClass]
public sealed class Mp3BundleIdentityEndpointTests
{
    private const int Tracks = 20;

    private static byte[] Frame(string id, string text)
    {
        var payload = new[] { (byte)3 }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        return Encoding.ASCII.GetBytes(id)
            .Concat([(byte)0, (byte)0, (byte)(payload.Length >> 8), (byte)payload.Length, (byte)0, (byte)0])
            .Concat(payload).ToArray();
    }

    private static byte[] Id3Tag(string album, string artist)
    {
        var body = Frame("TALB", album).Concat(Frame("TPE1", artist)).ToArray();
        return "ID3"u8.ToArray()
            .Concat([(byte)3, (byte)0, (byte)0, (byte)((body.Length >> 21) & 0x7F), (byte)((body.Length >> 14) & 0x7F),
                (byte)((body.Length >> 7) & 0x7F), (byte)(body.Length & 0x7F)])
            .Concat(body).ToArray();
    }

    private static byte[] Audio(byte fill)
    {
        const int frameLength = 144 * 128 * 1000 / 44100;
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

    private static async Task<(FamilyLibrarianAppFactory Factory, Guid[] Ids)> SeedAsync(
        WebTestFixture fixture, int germanTrack)
    {
        var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
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
        var summary = (await scope.ServiceProvider.GetRequiredService<IWorkLookup>().FindAsync(workId, CancellationToken.None))!;

        var bundleId = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var track = 1; track <= Tracks; track++)
        {
            var album = track == germanTrack ? "Der kleine Hobbit (ungekürzt)" : summary.Title;
            await using var content = new MemoryStream(Id3Tag(album, summary.PrimaryAuthor!).Concat(Audio((byte)(0x10 + track))).ToArray());
            var staged = await store.WriteToQuarantineAsync(content, $"track{track}.mp3", 10_000_000, CancellationToken.None);
            var asset = new MediaAsset(
                workId, null, RequestMediaType.Audiobook, ".mp3", $"track{track}.mp3", staged.StoredFilename, staged.SizeBytes,
                staged.Sha256, staged.DetectedMimeType, formatId, null, DateTimeOffset.UtcNow,
                bundleId: bundleId, bundleSequence: track, bundleTrackCount: Tracks,
                identityPreConfirmed: true, origin: AssetOrigin.ExternalProvider);
            database.MediaAssets.Add(asset);
            ids.Add(asset.Id);
        }

        await database.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<AutomatedSecurityPipeline>().EvaluateBundleAsync(ids, CancellationToken.None);
        return (factory, [.. ids]);
    }

    private static async Task<List<MediaAsset>> LoadAsync(FamilyLibrarianAppFactory factory, Guid[] ids)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.MediaAssets.AsNoTracking().Where(asset => ids.Contains(asset.Id)).OrderBy(asset => asset.BundleSequence).ToListAsync();
    }

    [TestMethod]
    public async Task AnExternalSetWhoseEveryTrackTagMatchesClearsOnItsContent()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (factory, ids) = await SeedAsync(fixture, germanTrack: 0);
        await using var _ = factory;

        var assets = await LoadAsync(factory, ids);
        Assert.IsTrue(assets.All(asset => asset.StorageState == MediaAssetStorageState.Trusted && asset.IdentityHoldCode is null),
            string.Join(", ", assets.Select(asset => $"{asset.StorageState}/{asset.IdentityHoldCode}")));
    }

    [TestMethod]
    public async Task OneGermanTrackHoldsTheWholeSet()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var (factory, ids) = await SeedAsync(fixture, germanTrack: 7);
        await using var _ = factory;

        var assets = await LoadAsync(factory, ids);
        Assert.IsFalse(assets.Any(asset => asset.StorageState == MediaAssetStorageState.Trusted),
            "No sibling is approved while any track is held: " + string.Join(", ", assets.Select(asset => asset.StorageState)));
        var german = assets.Single(asset => asset.BundleSequence == 7);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, german.StorageState);
        Assert.IsNotNull(german.IdentityHoldCode);
    }
}
