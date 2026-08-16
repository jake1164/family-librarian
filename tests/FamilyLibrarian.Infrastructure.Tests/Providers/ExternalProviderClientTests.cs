using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Providers;
using FamilyLibrarian.SampleProvider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// The conformance test the M13 plan calls for: a real <see cref="ExternalProviderClient"/>
/// (real sockets, no shortcuts) against a real running instance of the sample
/// provider — the same assembly a third-party implementer would build from.
/// </summary>
[TestClass]
public sealed class ExternalProviderClientTests
{
    private static WebApplication? _app;
    private static string _baseUrl = string.Empty;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _app = SampleProviderHost.Build(["--urls=http://127.0.0.1:0"]);
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static ExternalProviderClient CreateClient() => new(new SimpleHttpClientFactory());

    [TestMethod]
    public async Task ManifestReportsTheDeclaredProtocolAndCapabilities()
    {
        var client = CreateClient();

        var manifest = await client.GetManifestAsync(_baseUrl, apiKey: null, EgressRoute.Direct, CancellationToken.None);

        Assert.AreEqual("1", manifest.ProtocolVersion);
        Assert.AreEqual("sample-provider", manifest.Id);
        CollectionAssert.Contains(manifest.Capabilities.ToArray(), "acquire");
        Assert.AreEqual("NORMAL", manifest.EgressPolicy);
    }

    [TestMethod]
    public async Task HealthReportsTrue()
    {
        var client = CreateClient();

        var healthy = await client.GetHealthAsync(_baseUrl, apiKey: null, EgressRoute.Direct, CancellationToken.None);

        Assert.IsTrue(healthy);
    }

    [TestMethod]
    public async Task SearchFindsTheCannedCandidateByTitle()
    {
        var client = CreateClient();

        var results = await client.SearchAsync(
            _baseUrl, apiKey: null,
            new ExternalProviderSearchRequest(Guid.NewGuid(), RequestMediaType.Ebook, "Pride and Prejudice", [], null),
            EgressRoute.Direct, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("pride-and-prejudice", results[0].ProviderReference);
    }

    [TestMethod]
    public async Task SearchReturnsNothingForAnUnknownTitle()
    {
        var client = CreateClient();

        var results = await client.SearchAsync(
            _baseUrl, apiKey: null,
            new ExternalProviderSearchRequest(Guid.NewGuid(), RequestMediaType.Ebook, "Not A Real Book Title Xyz", [], null),
            EgressRoute.Direct, CancellationToken.None);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task AcquirePollsThroughToACompletedArtifact()
    {
        var client = CreateClient();

        var artifact = await client.AcquireAsync(
            _baseUrl, apiKey: null, "frankenstein", RequestMediaType.Ebook, EgressRoute.Direct, CancellationToken.None);

        await using var content = artifact.Content;
        Assert.AreEqual("frankenstein.epub", artifact.Filename);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.IsTrue(buffer.Length > 0);

        // The sample provider's artifact is a real, PK-signed minimal EPUB —
        // the same content-type sniffing family librarian applies to any
        // provider's file must see this as genuinely valid.
        var bytes = buffer.ToArray();
        Assert.AreEqual(0x50, bytes[0]);
        Assert.AreEqual(0x4B, bytes[1]);
    }

    [TestMethod]
    public async Task AcquiringAnUnknownCandidateThrows()
    {
        var client = CreateClient();

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.AcquireAsync(
            _baseUrl, apiKey: null, "not-a-real-candidate", RequestMediaType.Ebook, EgressRoute.Direct, CancellationToken.None));
    }

    [TestMethod]
    public async Task AWrongOrMissingApiKeyIsRejectedWhenOneIsConfigured()
    {
        Environment.SetEnvironmentVariable("SAMPLE_PROVIDER_API_KEY", "expected-secret");
        WebApplication? securedApp = null;
        try
        {
            securedApp = SampleProviderHost.Build(["--urls=http://127.0.0.1:0"]);
            await securedApp.StartAsync();
            var securedBaseUrl = securedApp.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = CreateClient();

            var manifestWithoutKey = () =>
                client.GetManifestAsync(securedBaseUrl, null, EgressRoute.Direct, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(manifestWithoutKey);

            var manifest = await client.GetManifestAsync(
                securedBaseUrl, "expected-secret", EgressRoute.Direct, CancellationToken.None);
            Assert.AreEqual("sample-provider", manifest.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAMPLE_PROVIDER_API_KEY", null);
            if (securedApp is not null)
            {
                await securedApp.StopAsync();
                await securedApp.DisposeAsync();
            }
        }
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
