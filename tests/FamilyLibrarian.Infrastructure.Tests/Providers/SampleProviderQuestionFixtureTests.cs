using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.SampleProvider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// The Phase 5 requester-question fixtures the family-librarian-lab drives (<c>tests/test_acquisition_questions.py</c>).
/// They are fixtures only, switched on by SAMPLE_PROVIDER_QUESTION_FIXTURES, so the default catalog every other test
/// and lab case relies on is unchanged; this class pins both halves.
/// </summary>
[TestClass]
public sealed class SampleProviderQuestionFixtureTests
{
    private const string FixturesVariable = "SAMPLE_PROVIDER_QUESTION_FIXTURES";
    private static readonly string[] CollectionContents = ["Ember Road", "Cinder Gate", "Ashen Crown"];

    [TestMethod]
    public async Task ACollectionEpubIsValidAndItsTableOfContentsListsEveryContainedWork()
    {
        var bytes = SampleEpub.Build("The Lantern Cycle Books 1-3", "Mara Quillon", CollectionContents);

        var outcome = await new EpubValidator().ValidateAsync(Asset(), new MemoryStream(bytes), CancellationToken.None);
        Assert.IsTrue(outcome.IsValid, outcome.Message);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var facts = EpubProfileInspector.Inspect(archive, "OEBPS/content.opf");
        CollectionAssert.AreEqual(
            CollectionContents,
            facts.Where(fact => fact.Code == EvidenceReasonCodes.ArtifactProfile.TocEntry).Select(fact => fact.Value!).ToArray());
        // Every table-of-contents target exists, so the completeness check does not hold the collection.
        Assert.IsFalse(
            EpubCompletenessInspector.Inspect(archive, "OEBPS/content.opf", ["The Lantern Cycle Books 1-3"]).Any(fact =>
                fact.Code == EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing),
            "A fixture whose table of contents points at missing files would be held for the wrong reason.");
    }

    [TestMethod]
    public async Task WithoutTheFixtureSwitchTheCatalogIsUnchangedAndThereIsNoControlPlane()
    {
        await using var provider = await StartAsync(fixtures: false);

        Assert.IsEmpty(await SearchAsync(provider, "Cinder Gate", "ebook"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await provider.Client.GetAsync("/test/state")).StatusCode);
        var manifest = await provider.Client.GetFromJsonAsync<JsonNode>("/manifest");
        Assert.AreEqual(1, manifest!["capabilities"]!["mediaTypes"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task FixturesAnswerOnlyTheirTitlesAndMediaTypeAndTheStandaloneAppearsOnlyWhenEnabled()
    {
        await using var provider = await StartAsync(fixtures: true);

        var collection = await SearchAsync(provider, "Cinder Gate", "ebook");
        Assert.HasCount(1, collection);
        Assert.AreEqual("lantern-books-1-3", collection[0]!["providerReference"]!.GetValue<string>());
        Assert.IsTrue(collection[0]!["release"]!["isCollection"]!.GetValue<bool>());
        Assert.IsEmpty(await SearchAsync(provider, "Cinder Gate", "audiobook"));

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await provider.Client.PostAsync("/test/catalog/lantern-cinder-standalone/enable", null)).StatusCode);
        Assert.HasCount(2, await SearchAsync(provider, "Cinder Gate", "ebook"));

        Assert.AreEqual(HttpStatusCode.NoContent, (await provider.Client.PostAsync("/test/reset", null)).StatusCode);
        Assert.HasCount(1, await SearchAsync(provider, "Cinder Gate", "ebook"));
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await provider.Client.PostAsync("/test/catalog/no-such-fixture/enable", null)).StatusCode);
    }

    [TestMethod]
    public async Task TheQuestionKindFixturesCarryTheirDistinguishingEvidence()
    {
        await using var provider = await StartAsync(fixtures: true);

        var abridged = (await SearchAsync(provider, "Windfall Hollow", "audiobook")).Single()!;
        Assert.IsTrue(abridged["release"]!["isAbridged"]!.GetValue<bool>());
        Assert.AreEqual("m4b", abridged["release"]!["format"]!.GetValue<string>());

        var foreign = (await SearchAsync(provider, "Hollowmere", "ebook")).Single()!;
        Assert.AreEqual("fr", foreign["edition"]!["language"]!.GetValue<string>());

        var parts = (await SearchAsync(provider, "Saltmarsh Light", "audiobook")).Single()!;
        Assert.AreEqual("Saltmarsh Light (1 of 2)", parts["release"]!["name"]!.GetValue<string>());

        var box = (await SearchAsync(provider, "Ember Road", "ebook")).Single()!;
        Assert.AreEqual("The Lantern Cycle Box Set", box["work"]!["title"]!.GetValue<string>());

        // The audiobook fixtures exist to be searched, not fetched.
        var acquire = await provider.Client.PostAsJsonAsync(
            "/acquire", new { candidateReference = "windfall-abridged" });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, acquire.StatusCode);
    }

    private static async Task<JsonArray> SearchAsync(RunningProvider provider, string title, string mediaType)
    {
        var response = await provider.Client.PostAsJsonAsync(
            "/search", new { requestId = Guid.NewGuid(), mediaType, work = new { title } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["candidates"]!.AsArray();
    }

    // The switch is read once, synchronously, inside Build; set it only for that call so no other test sees it.
    private static async Task<RunningProvider> StartAsync(bool fixtures)
    {
        WebApplication app;
        lock (typeof(SampleProviderQuestionFixtureTests))
        {
            var previous = Environment.GetEnvironmentVariable(FixturesVariable);
            Environment.SetEnvironmentVariable(FixturesVariable, fixtures ? "1" : null);
            try
            {
                app = SampleProviderHost.Build(["--urls=http://127.0.0.1:0"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable(FixturesVariable, previous);
            }
        }

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RunningProvider(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static MediaAsset Asset() => new(
        Guid.NewGuid(), editionId: null, RequestMediaType.Ebook, ".epub", "book.epub", $"{Guid.NewGuid():N}.epub",
        sizeBytes: 1024, sha256: new string('a', 64), detectedMimeType: "application/epub+zip", Guid.NewGuid(),
        sourceAcquisitionCandidateId: null, new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    private sealed class RunningProvider(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
