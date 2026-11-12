using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A6-UPGRADE-1 U-2: FL's own Audiobookshelf and CWA client classes against the real applications, because the upgrade removes a
/// delivered book on the strength of what these calls return. Opt-in (needs Docker and the images): set
/// <c>FAMILY_LIBRARIAN_DESTINATION_TESTS=1</c>. Audiobookshelf is pinned to the version measured on 2026-11-11; CWA uses its latest image
/// because it publishes no stable tags, so a failure here after a CWA upgrade is the early warning this test exists to give.
/// </summary>
[TestClass]
public sealed class DestinationClientContainerTests
{
    private const string AbsImage = "advplyr/audiobookshelf:2.36.0";
    private const string CwaImage = "crocodilestick/calibre-web-automated:latest";

    private static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_DESTINATION_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_DESTINATION_TESTS=1 (Docker and the Audiobookshelf and CWA images required) to run the destination client tests.");
    }

    private sealed class PlainProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class FixedSettings(AudiobookshelfSettings settings) : IAudiobookshelfSettingsStore
    {
        public Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult<AudiobookshelfSettings?>(settings);

        public Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoMatcher : IBookMatchService
    {
        public Task<BookMatchResult> ResolveUniqueAsync(string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken, string? acceptedLanguage = null) =>
            throw new NotSupportedException();

        public Task<BookMatchResult> MatchByTitleAuthorAsync(string title, string? author, IReadOnlyList<CandidateBook> candidates, CancellationToken cancellationToken, string? acceptedLanguage = null) =>
            throw new NotSupportedException();
    }

    private static IHttpClientFactory HttpFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        // Exactly as the host registers it: login success is read from a redirect, so redirects and cookies are handled by hand.
        services.AddHttpClient(CwaEreaderSessionClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    // ---- Audiobookshelf -------------------------------------------------------------------------------------------------------------

    private static AudiobookshelfApiClient AbsClient(string baseUrl, string libraryId, string folderId, string token)
    {
        var settings = new AudiobookshelfSettings(DateTimeOffset.UtcNow);
        settings.SetSettings(baseUrl, null, libraryId, folderId, null, DateTimeOffset.UtcNow);
        settings.SetApiToken(token, 1, null, null, DateTimeOffset.UtcNow);
        settings.SetEnabled(true, null, DateTimeOffset.UtcNow);
        return new AudiobookshelfApiClient(HttpFactory(), new FixedSettings(settings), new PlainProtector(), new NoMatcher());
    }

    private static async Task<T> PollAsync<T>(Func<Task<T?>> attempt, int seconds = 90) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await attempt() is { } found) return found;
            await Task.Delay(2000);
        }

        throw new AssertFailedException("Timed out waiting for the application to reflect the change.");
    }

    [TestMethod]
    public async Task OurAudiobookshelfClientUploadsFindsRenamesAndHardDeletesAndFailsClosedWithoutThePermission()
    {
        RequireOptIn();
        await using var container = new ContainerBuilder(AbsImage)
            .WithPortBinding(80, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(80).ForPath("/status")))
            .Build();
        await container.StartAsync();
        var baseUrl = $"http://{container.Hostname}:{container.GetMappedPublicPort(80)}";
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        (await http.PostAsJsonAsync("/init", new { newRoot = new { username = "root", password = "pv-test-pass" } })).EnsureSuccessStatusCode();
        async Task<string> LoginAsync(string user, string password)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = JsonContent.Create(new { username = user, password }) };
            request.Headers.Add("x-return-tokens", "true");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var user_ = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["user"]!;
            return (user_["accessToken"] ?? user_["token"])!.GetValue<string>();
        }

        var rootToken = await LoginAsync("root", "pv-test-pass");
        using var admin = new HttpClient { BaseAddress = new Uri(baseUrl) };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rootToken);
        Assert.AreEqual(0, (await container.ExecAsync(["mkdir", "-p", "/audiobooks"])).ExitCode);
        var libraryResponse = await admin.PostAsJsonAsync("/api/libraries", new { name = "tests", folders = new[] { new { fullPath = "/audiobooks" } }, mediaType = "book" });
        libraryResponse.EnsureSuccessStatusCode();
        var library = JsonNode.Parse(await libraryResponse.Content.ReadAsStringAsync())!;
        var libraryId = library["id"]!.GetValue<string>();
        var folderId = library["folders"]![0]!["id"]!.GetValue<string>();
        Assert.AreEqual(0, (await container.ExecAsync(["sh", "-c", "ffmpeg -loglevel error -f lavfi -i sine=frequency=440:duration=2 -q:a 9 /tmp/a.mp3 && ffmpeg -loglevel error -f lavfi -i sine=frequency=550:duration=2 -q:a 9 /tmp/b.mp3"])).ExitCode);
        var oldAudio = await container.ReadFileAsync("/tmp/a.mp3");
        var newAudio = await container.ReadFileAsync("/tmp/b.mp3");
        Task ScanAsync() => admin.PostAsync($"/api/libraries/{libraryId}/scan", null);

        var client = AbsClient(baseUrl, libraryId, folderId, rootToken);
        Assert.IsTrue(await client.CanDeleteAsync(CancellationToken.None), "A root account may delete.");

        const string RealTitle = "Container Test Book";
        var temporary = LibraryUpgradeTitle(RealTitle);
        Assert.IsTrue((await client.UploadAsync(new MemoryStream(oldAudio), "part.mp3", RealTitle, "Test Author", CancellationToken.None)).Succeeded);
        await ScanAsync();
        var oldId = await PollAsync(async () => await client.FindItemIdByExactTitleAsync(RealTitle, CancellationToken.None));

        Assert.IsTrue((await client.UploadAsync(new MemoryStream(newAudio), "part.mp3", temporary, "Test Author", CancellationToken.None)).Succeeded);
        await ScanAsync();
        var newId = await PollAsync(async () => await client.FindItemIdByExactTitleAsync(temporary, CancellationToken.None));
        Assert.AreNotEqual(oldId, newId, "Under its own title the better copy is its own item, not merged into the old one.");
        Assert.AreEqual(oldId, await client.FindItemIdByExactTitleAsync(RealTitle, CancellationToken.None), "Exact-title lookup tells the two apart.");
        Assert.IsNull(await client.FindItemIdByExactTitleAsync("Container Test", CancellationToken.None), "A partial title is not a match.");

        // An account without the delete permission must never be able to retract anything.
        (await admin.PostAsJsonAsync("/api/users", new
        {
            username = "limited", password = "pv-limited", type = "user", isActive = true,
            permissions = new { download = true, update = false, delete = false, upload = true, accessAllLibraries = true, accessAllTags = true }
        })).EnsureSuccessStatusCode();
        var limited = AbsClient(baseUrl, libraryId, folderId, await LoginAsync("limited", "pv-limited"));
        Assert.IsFalse(await limited.CanDeleteAsync(CancellationToken.None), "No delete permission is reported as such.");
        Assert.IsFalse(await limited.DeleteItemHardAsync(oldId, CancellationToken.None), "Delete without the permission fails closed.");
        Assert.AreEqual(oldId, await client.FindItemIdByExactTitleAsync(RealTitle, CancellationToken.None), "The old item is untouched.");

        Assert.IsTrue(await client.DeleteItemHardAsync(oldId, CancellationToken.None));
        var files = (await container.ExecAsync(["find", "/audiobooks", "-type", "f"])).Stdout;
        Assert.IsTrue(files.Contains("Test Author", StringComparison.Ordinal) && files.Contains("(upgrade", StringComparison.Ordinal), "The better copy's files remain.");
        Assert.IsFalse(files.Contains($"/{RealTitle}/", StringComparison.Ordinal), "A hard delete removes the old copy's files, so the next scan cannot bring it back.");
        await ScanAsync();
        await Task.Delay(8000);
        Assert.IsNull(await client.FindItemIdByExactTitleAsync(RealTitle, CancellationToken.None), "The deleted item did not come back after a scan.");

        Assert.IsTrue(await client.RenameItemAsync(newId, RealTitle, CancellationToken.None));
        Assert.AreEqual(newId, await PollAsync(async () => await client.FindItemIdByExactTitleAsync(RealTitle, CancellationToken.None)), "The better copy now carries the real title.");
        Assert.IsNull(await client.FindItemIdByExactTitleAsync(temporary, CancellationToken.None));
    }

    private static string LibraryUpgradeTitle(string title) => FamilyLibrarian.Application.Upgrades.LibraryUpgradeCopyPublisher.TemporaryTitle(title, Guid.NewGuid());

    // ---- Calibre-Web-Automated ------------------------------------------------------------------------------------------------------

    private static async Task<string> SqliteAsync(IContainer container, string database, string sql)
    {
        var result = await container.ExecAsync(["sqlite3", database, sql]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        return result.Stdout.Trim();
    }

    [TestMethod]
    public async Task OurCwaClientDeletesABookThroughTheWebSessionAndFailsClosedOnEverythingElse()
    {
        RequireOptIn();
        await using var container = new ContainerBuilder(CwaImage)
            .WithEnvironment("PUID", "1000").WithEnvironment("PGID", "1000")
            .WithPortBinding(8083, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8083).ForPath("/login")))
            .Build();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await container.StartAsync(startup.Token);
        var settings = new CwaSettings(DateTimeOffset.UtcNow);
        settings.SetSettings(CwaTransportMode.Local, "/unused", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey,
            $"http://{container.Hostname}:{container.GetMappedPublicPort(8083)}", null, null, "admin", null, DateTimeOffset.UtcNow);
        var client = new CwaEreaderSessionClient(HttpFactory(), NullLogger<CwaEreaderSessionClient>.Instance);

        await container.CopyAsync(EpubTestFixture.BuildMinimalEpubBytes("Delete Me Please"), "/cwa-book-ingest/delete-me.epub");
        var bookId = await PollAsync(async () =>
        {
            var found = (await container.ExecAsync(["sqlite3", "/calibre-library/metadata.db", "select id from books where title like 'Delete Me%'"])).Stdout.Trim();
            return found.Length > 0 && found.All(char.IsAsciiDigit) ? found : null;
        }, seconds: 180);
        async Task<string> CountAsync() => await SqliteAsync(container, "/calibre-library/metadata.db", $"select count(*) from books where id = {bookId}");
        Assert.AreEqual("1", await CountAsync(), "Precondition: CWA imported the book.");

        Assert.IsFalse((await client.DeleteBookAsync(settings, "admin", "wrong-password", bookId, CancellationToken.None)).Succeeded, "A wrong password fails closed.");
        Assert.IsFalse((await client.DeleteBookAsync(settings, "admin", "admin123", "1; drop table books", CancellationToken.None)).Succeeded, "A non-numeric id is refused before any request.");
        Assert.IsFalse((await client.DeleteBookAsync(settings, "admin", "admin123", "99999", CancellationToken.None)).Succeeded, "A book that does not exist is not reported deleted.");
        Assert.AreEqual("1", await CountAsync(), "None of the refused calls removed anything.");

        // An account without the delete permission: take the administrator and delete-books roles away, keep download/upload/edit.
        var originalRole = await SqliteAsync(container, "/config/app.db", "select role from user where name = 'admin'");
        await SqliteAsync(container, "/config/app.db", "update user set role = 14 where name = 'admin'");
        var refused = await client.DeleteBookAsync(settings, "admin", "admin123", bookId, CancellationToken.None);
        Assert.IsFalse(refused.Succeeded, "Without the permission CWA must not report success. " + refused.Message);
        Assert.AreEqual("1", await CountAsync(), "No permission, no removal.");

        await SqliteAsync(container, "/config/app.db", $"update user set role = {originalRole} where name = 'admin'");
        var deleted = await client.DeleteBookAsync(settings, "admin", "admin123", bookId, CancellationToken.None);
        Assert.IsTrue(deleted.Succeeded, deleted.Message);
        Assert.AreEqual("0", await CountAsync(), "The record is gone.");
        Assert.IsFalse((await container.ExecAsync(["find", "/calibre-library", "-name", "*.epub"])).Stdout.Contains(".epub", StringComparison.Ordinal), "The file is gone too.");
    }
}
