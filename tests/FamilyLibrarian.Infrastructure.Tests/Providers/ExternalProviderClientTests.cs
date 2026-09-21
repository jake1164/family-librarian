using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
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

        CollectionAssert.Contains(manifest.ProtocolVersions.ToArray(), "2");
        Assert.AreEqual("2", ProtocolVersionNegotiation.Negotiate(manifest.ProtocolVersions));
        Assert.IsFalse(string.IsNullOrEmpty(manifest.InstanceId));
        Assert.AreEqual("sample-provider", manifest.Id);
        CollectionAssert.Contains(manifest.Capabilities.Operations.ToArray(), "acquire");
        Assert.AreEqual("NORMAL", manifest.EgressPolicy);
    }

    [TestMethod]
    public async Task HealthReportsAvailableOperations()
    {
        var client = CreateClient();

        var health = await client.GetHealthAsync(_baseUrl, apiKey: null, EgressRoute.Direct, CancellationToken.None);

        Assert.IsTrue(health.IsHealthy);
        Assert.AreEqual(ProviderOperationalStatus.Available, health.Search);
        Assert.AreEqual(ProviderOperationalStatus.Available, health.Acquire);
    }

    [TestMethod]
    public async Task SearchFindsTheCannedCandidateByTitle()
    {
        var client = CreateClient();

        var results = await client.SearchAsync(
            _baseUrl, apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Pride and Prejudice", null, [], [], [])),
            EgressRoute.Direct, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("pride-and-prejudice", results[0].ProviderReference);
        Assert.AreEqual("Jane Austen", results[0].Work.Authors[0].Name);
        Assert.IsNotNull(results[0].Edition);
        Assert.AreEqual(ExternalProviderDrmStatus.None, results[0].Release!.DrmStatus);
    }

    [TestMethod]
    public async Task SearchReturnsNothingForAnUnknownTitle()
    {
        var client = CreateClient();

        var results = await client.SearchAsync(
            _baseUrl, apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Not A Real Book Title Xyz", null, [], [], [])),
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
    public async Task SubmitAcquireWithTheSameIdempotencyKeyReturnsTheSameJob()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(Guid.NewGuid(), "frankenstein", null, null, RequestMediaType.Ebook);
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var first = await client.SubmitAcquireAsync(_baseUrl, null, request, idempotencyKey, EgressRoute.Direct, CancellationToken.None);
        var second = await client.SubmitAcquireAsync(_baseUrl, null, request, idempotencyKey, EgressRoute.Direct, CancellationToken.None);

        Assert.AreEqual(ProviderAcquireOutcome.Accepted, first.Outcome);
        Assert.AreEqual(first.JobId, second.JobId);
    }

    [TestMethod]
    public async Task AcquireJobReachesWaitingUserInteractionBeforeCompleting()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(
            Guid.NewGuid(), "the-time-machine", null, null, RequestMediaType.Ebook);

        var submission = await client.SubmitAcquireAsync(
            _baseUrl, null, request, Guid.NewGuid().ToString("N"), EgressRoute.Direct, CancellationToken.None);
        Assert.AreEqual(ProviderAcquireOutcome.Accepted, submission.Outcome);

        ExternalProviderJobStatus? sawWaiting = null;
        ExternalProviderJobStatus status;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            status = await client.GetAcquireStatusAsync(
                _baseUrl, null, submission.JobId!, EgressRoute.Direct, CancellationToken.None);
            if (status.State == ProviderAcquisitionJobLifecycleState.Waiting)
            {
                sawWaiting = status;
            }

            if (status.State is not (ProviderAcquisitionJobLifecycleState.Completed or ProviderAcquisitionJobLifecycleState.Failed))
            {
                await Task.Delay(200);
            }
        }
        while (status.State is not (ProviderAcquisitionJobLifecycleState.Completed or ProviderAcquisitionJobLifecycleState.Failed)
            && DateTimeOffset.UtcNow < deadline);

        Assert.IsNotNull(sawWaiting, "The job never reported state=waiting.");
        Assert.AreEqual("user-interaction", sawWaiting!.Phase);
        Assert.IsNotNull(sawWaiting.Interaction);
        Assert.AreEqual("browser", sawWaiting.Interaction!.Type);
        Assert.AreEqual(true, sawWaiting.Interaction.ResumeSupported);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Completed, status.State);
    }

    [TestMethod]
    public async Task CompletedJobExposesMultipleOutputsWithChecksums()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(
            Guid.NewGuid(), "pride-and-prejudice", null, null, RequestMediaType.Ebook);

        var submission = await client.SubmitAcquireAsync(
            _baseUrl, null, request, Guid.NewGuid().ToString("N"), EgressRoute.Direct, CancellationToken.None);

        ExternalProviderJobStatus status;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            status = await client.GetAcquireStatusAsync(
                _baseUrl, null, submission.JobId!, EgressRoute.Direct, CancellationToken.None);
            if (status.State != ProviderAcquisitionJobLifecycleState.Completed)
            {
                await Task.Delay(200);
            }
        }
        while (status.State != ProviderAcquisitionJobLifecycleState.Completed && DateTimeOffset.UtcNow < deadline);

        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Completed, status.State);

        var outputs = await client.ListOutputsAsync(_baseUrl, null, submission.JobId!, EgressRoute.Direct, CancellationToken.None);
        Assert.AreEqual(2, outputs.Count);

        var primary = outputs.Single(output => output.OutputId == "primary");
        Assert.AreEqual(ProviderOutputKind.File, primary.Kind);
        Assert.IsFalse(string.IsNullOrEmpty(primary.ChecksumsJson));

        var artifact = await client.GetOutputAsync(
            _baseUrl, null, submission.JobId!, "primary", EgressRoute.Direct, CancellationToken.None);
        await using var content = artifact.Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        Assert.AreEqual(0x50, bytes[0]);
        Assert.AreEqual(0x4B, bytes[1]);
    }

    [TestMethod]
    public async Task CancelThenDeleteAreBothAcceptedForAnInFlightJob()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(Guid.NewGuid(), "frankenstein", null, null, RequestMediaType.Ebook);
        var submission = await client.SubmitAcquireAsync(
            _baseUrl, null, request, Guid.NewGuid().ToString("N"), EgressRoute.Direct, CancellationToken.None);

        await client.CancelAcquireAsync(_baseUrl, null, submission.JobId!, EgressRoute.Direct, CancellationToken.None);
        var status = await client.GetAcquireStatusAsync(_baseUrl, null, submission.JobId!, EgressRoute.Direct, CancellationToken.None);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, status.State);

        await client.DeleteAcquireAsync(_baseUrl, null, submission.JobId!, EgressRoute.Direct, CancellationToken.None);
    }

    [TestMethod]
    public async Task SearchReturnsRichWorkEditionAndSeriesEvidence()
    {
        var client = CreateClient();

        var results = await client.SearchAsync(
            _baseUrl, apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Debt of Honor", null, [], [], [])),
            EgressRoute.Direct, CancellationToken.None);

        var candidate = results.Single();
        Assert.AreEqual("Tom Clancy", candidate.Work.Authors[0].Name);
        Assert.AreEqual("Jack Ryan", candidate.Work.Series[0].Name);
        Assert.AreEqual("6", candidate.Work.Series[0].Position);
        Assert.AreEqual("Putnam", candidate.Edition!.Publisher);
        Assert.AreEqual(1994, candidate.Edition.PublicationYear);
        Assert.AreEqual("rev-1", candidate.CandidateRevision);
    }

    [TestMethod]
    public async Task SearchReturnsAnIsCollectionFlagThatExternalReleasePolicyRejects()
    {
        var client = CreateClient();

        var results = await client.SearchAsync(
            _baseUrl, apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Jack Ryan Omnibus", null, [], [], [])),
            EgressRoute.Direct, CancellationToken.None);

        var candidate = results.Single();
        Assert.AreEqual(true, candidate.Release!.IsCollection);

        var verdict = ExternalReleasePolicy.Evaluate(candidate.Release, RequestMediaType.Ebook);
        Assert.IsTrue(verdict.RequiresConfirmation);
    }

    [TestMethod]
    public async Task SubmitAcquireWithAStaleCandidateRevisionReturnsCandidateChanged()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(
            Guid.NewGuid(), "debt-of-honor", CandidateRevision: "rev-1", AcquireToken: null, RequestMediaType.Ebook);

        var submission = await client.SubmitAcquireAsync(
            _baseUrl, apiKey: null, request, Guid.NewGuid().ToString("N"), EgressRoute.Direct, CancellationToken.None);

        // retryable:false scopes to "don't retry this exact call with this
        // stale revision" -- not "the book request is dead"; that
        // distinction lives in the caller (DirectAcquisitionService invalidates
        // and re-searches rather than giving up), not in this outcome itself.
        Assert.AreEqual(ProviderAcquireOutcome.CandidateChanged, submission.Outcome);
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
