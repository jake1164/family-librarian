using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Providers;
using FamilyLibrarian.SampleProvider;
using System.Net;
using System.Text.Json.Nodes;
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
    private static readonly string[] ExpectedEbookFormats = ["epub", "mobi"];
    private static WebApplication? _app;
    private static string _baseUrl = string.Empty;

    // The sample provider's job timeline is measured in stages (waiting for two,
    // running until four, or a plain job completing at three). The deployed
    // default is a real second per stage; a quarter second still leaves a wide
    // window to observe each state while keeping the polling tests fast. The
    // client polls well inside one stage so it never oversleeps a transition.
    private const int JobStageMilliseconds = 250;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _app = SampleProviderHost.Build(
            ["--urls=http://127.0.0.1:0", $"--SampleProvider:JobStageMilliseconds={JobStageMilliseconds}"]);
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

    private static ExternalProviderClient CreateClient() => new(new SimpleHttpClientFactory(), PollInterval);

    [TestMethod]
    public async Task ManifestReportsTheDeclaredProtocolAndCapabilities()
    {
        var client = CreateClient();

        var manifest = await client.GetManifestAsync(_baseUrl, apiKey: null, CancellationToken.None);

        CollectionAssert.Contains(manifest.ProtocolVersions.ToArray(), "2");
        Assert.AreEqual("2", ProtocolVersionNegotiation.Negotiate(manifest.ProtocolVersions));
        Assert.IsFalse(string.IsNullOrEmpty(manifest.InstanceId));
        Assert.AreEqual("sample-provider", manifest.Id);
        CollectionAssert.Contains(manifest.Capabilities.Operations.ToArray(), "acquire");
    }

    [TestMethod]
    public async Task LegacyManifestEgressFieldDoesNotAffectProviderConnection()
    {
        var factory = new RecordingHttpClientFactory(new StaticSearchHandler(
            """{"protocolVersions":["2"],"id":"legacy-provider","egressPolicy":"PRIVATE_REQUIRED"}"""));
        var client = new ExternalProviderClient(factory);

        var manifest = await client.GetManifestAsync("http://provider.test", apiKey: null, CancellationToken.None);

        Assert.AreEqual("legacy-provider", manifest.Id);
        Assert.IsNotNull(factory.CreatedClient);
    }

    [TestMethod]
    public async Task HealthReportsAvailableOperations()
    {
        var client = CreateClient();

        var health = await client.GetHealthAsync(_baseUrl, apiKey: null, CancellationToken.None);

        Assert.IsTrue(health.IsHealthy);
        Assert.AreEqual(ProviderOperationalStatus.Available, health.Search);
        Assert.AreEqual(ProviderOperationalStatus.Available, health.Acquire);
    }

    private static async Task<ExternalProviderHealth> GetHealthFromBodyAsync(string body)
    {
        var client = new ExternalProviderClient(new RecordingHttpClientFactory(new StaticSearchHandler(body)));
        return await client.GetHealthAsync("http://provider.test", apiKey: null, CancellationToken.None);
    }

    [TestMethod]
    public async Task HealthParsesReportedIssues()
    {
        var health = await GetHealthFromBodyAsync(
            """
            {"status":"degraded","operations":{"search":"degraded","acquire":"available"},
             "issues":[
               {"operation":"search","code":"no-indexers","message":"No enabled indexer supports search."},
               {"operation":"GENERAL","message":"  Upstream\r\nis slow. "}]}
            """);

        Assert.AreEqual(ProviderOperationalStatus.Degraded, health.Search);
        Assert.AreEqual(2, health.ReportedIssues.Count);
        Assert.AreEqual(new ProviderHealthIssue("search", "no-indexers", "No enabled indexer supports search."), health.ReportedIssues[0]);
        Assert.AreEqual(new ProviderHealthIssue("general", null, "Upstream is slow."), health.ReportedIssues[1]);
    }

    [TestMethod]
    public async Task HealthWithoutIssuesIsTheUnchangedPreChangeBody()
    {
        var health = await GetHealthFromBodyAsync(
            """{"status":"degraded","operations":{"search":"available","acquire":"unavailable"}}""");

        Assert.AreEqual(ProviderOperationalStatus.Available, health.Search);
        Assert.AreEqual(ProviderOperationalStatus.Unavailable, health.Acquire);
        Assert.AreEqual(0, health.ReportedIssues.Count);

        var bare = await GetHealthFromBodyAsync(string.Empty);
        Assert.IsTrue(bare.IsFullyOperational);
        Assert.AreEqual(0, bare.ReportedIssues.Count);
    }

    [TestMethod]
    [DataRow("""{"status":"degraded","issues":"not an array"}""")]
    [DataRow("""{"status":"degraded","issues":{"operation":"search","message":"x"}}""")]
    [DataRow("""{"status":"degraded","issues":[1,"two",null,[],{"operation":7,"message":"x"},{"operation":"search"},{"operation":"search","message":{"a":1}},{"operation":"search","message":"   "}]}""")]
    public async Task MalformedIssuesAreIgnoredAndNeverFailTheHealthProbe(string body)
    {
        var health = await GetHealthFromBodyAsync(body);

        Assert.AreEqual(ProviderHealthStatus.Degraded, health.Status);
        Assert.AreEqual(0, health.ReportedIssues.Count);
    }

    [TestMethod]
    public async Task UnknownIssueOperationsAndFieldsAreIgnored()
    {
        var health = await GetHealthFromBodyAsync(
            """
            {"status":"degraded","issues":[
              {"operation":"delete-everything","message":"dropped"},
              {"operation":"acquire","message":"kept","severity":"high","extensions":{"a":1}}]}
            """);

        Assert.AreEqual(1, health.ReportedIssues.Count);
        Assert.AreEqual("acquire", health.ReportedIssues[0].Operation);
        Assert.AreEqual("kept", health.ReportedIssues[0].Message);
    }

    [TestMethod]
    public async Task OversizedIssuesAreCapped()
    {
        var entries = string.Join(",", Enumerable.Range(0, 20).Select(index =>
            $$"""{"operation":"search","code":"{{new string('c', 200)}}","message":"{{new string('m', 5_000)}}{{index}}"}"""));

        var health = await GetHealthFromBodyAsync($$"""{"status":"degraded","issues":[{{entries}}]}""");

        Assert.AreEqual(ExternalProviderClient.MaxHealthIssues, health.ReportedIssues.Count);
        Assert.IsTrue(health.ReportedIssues.All(issue =>
            issue.Message.Length == ExternalProviderClient.MaxHealthIssueMessageLength
            && issue.Code!.Length == ExternalProviderClient.MaxHealthIssueCodeLength));
    }

    [TestMethod]
    public async Task IssueControlAndFormatCharactersAreStrippedAndMarkupIsLeftAsLiteralText()
    {
        // ‮ is a bidi override (a Unicode "format" char), \u0007 a bell,
        // \u0000 a NUL, and \t/\n are whitespace controls. A message holding an
        // unpaired surrogate escape cannot be decoded at all and is dropped.
        var health = await GetHealthFromBodyAsync(
            """
            {"status":"degraded","issues":[
              {"operation":"search","code":"a\u0007b","message":"x\u0000‮y\tz\n<b>w</b>"},
              {"operation":"search","message":"lone \ud800 surrogate"}]}
            """);

        Assert.AreEqual(1, health.ReportedIssues.Count);
        Assert.AreEqual("ab", health.ReportedIssues[0].Code);
        Assert.AreEqual("xy z <b>w</b>", health.ReportedIssues[0].Message);
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
            CancellationToken.None);

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
            CancellationToken.None);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchUsesCallerCancellationRatherThanTheShortControlPlaneTimeout()
    {
        var handler = new HoldingSearchHandler();
        var factory = new RecordingHttpClientFactory(handler);
        var client = new ExternalProviderClient(factory);

        var search = client.SearchAsync(
            "http://provider.test", apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Slow but valid", null, [], [], [])),
            CancellationToken.None);

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(factory.CreatedClient);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, factory.CreatedClient.Timeout);

        handler.Release.TrySetResult();
        var results = await search;
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchOmitsUnspecifiedOptionalConstraintsInsteadOfSendingJsonNull()
    {
        var handler = new CapturingSearchHandler();
        var client = new ExternalProviderClient(new RecordingHttpClientFactory(handler));

        await client.SearchAsync(
            "http://provider.test", apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("The Cardinal of the Kremlin", null, [], [], []),
                Constraints: new ExternalProviderSearchConstraints(Formats: ["epub", "mobi"])),
            CancellationToken.None);

        Assert.IsNotNull(handler.Payload);
        var constraints = handler.Payload!["constraints"]!.AsObject();
        Assert.IsNull(constraints["languages"]);
        Assert.IsNull(constraints["excludeCollections"]);
        CollectionAssert.AreEqual(ExpectedEbookFormats, constraints["formats"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    [DataRow(" Example indexer · usenet · 679 grabs ", "Example indexer · usenet · 679 grabs")]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow(" \r\n\t\u0000 ", null)]
    [DataRow("Example\r\n indexer\t ·  usenet\u0000\u0007", "Example indexer · usenet")]
    [DataRow("<script>alert(1)</script> & <b>origin</b>", "<script>alert(1)</script> & <b>origin</b>")]
    public async Task SearchNormalizesAdministratorSourceSummary(string? supplied, string? expected)
    {
        var candidate = new System.Text.Json.Nodes.JsonObject
        {
            ["providerReference"] = "origin",
            ["work"] = new System.Text.Json.Nodes.JsonObject { ["title"] = "Example" }
        };
        if (supplied is not null)
        {
            candidate["sourceSummary"] = supplied;
        }

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["candidates"] = new System.Text.Json.Nodes.JsonArray(candidate)
        };
        var client = new ExternalProviderClient(new RecordingHttpClientFactory(new StaticSearchHandler(payload.ToJsonString())));
        var results = await client.SearchAsync("http://provider.test", null,
            new ExternalProviderSearchRequest(Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Any", null, [], [], [])), CancellationToken.None);
        Assert.AreEqual(expected, results.Single().SourceSummary);
    }

    [TestMethod]
    public async Task SearchTruncatesAdministratorSourceSummary()
    {
        var client = new ExternalProviderClient(new RecordingHttpClientFactory(new StaticSearchHandler(
            "{\"candidates\":[{\"providerReference\":\"long\",\"sourceSummary\":\"" + new string('x', 140) + "\"}]}")));
        var results = await client.SearchAsync("http://provider.test", null,
            new ExternalProviderSearchRequest(Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Any", null, [], [], [])), CancellationToken.None);
        Assert.AreEqual(new string('x', 120), results.Single().SourceSummary);
    }

    [TestMethod]
    public async Task SearchAcceptsOnlySafeProviderInspectionUris()
    {
        var handler = new StaticSearchHandler("""
            { "candidates": [
              { "providerReference": "safe", "inspectionUrl": "https://source.example.test/item/safe", "work": { "title": "Safe" } },
              { "providerReference": "unsafe", "inspectionUrl": "javascript:alert(1)", "work": { "title": "Unsafe" } },
              { "providerReference": "credentialed", "inspectionUrl": "https://user:secret@source.example.test/item", "work": { "title": "Credentialed" } }
            ] }
            """);
        var client = new ExternalProviderClient(new RecordingHttpClientFactory(handler));

        var results = await client.SearchAsync(
            "http://provider.test", apiKey: null,
            new ExternalProviderSearchRequest(
                Guid.NewGuid(), RequestMediaType.Ebook,
                new ExternalProviderWorkEvidence("Any", null, [], [], [])),
            CancellationToken.None);

        Assert.AreEqual("https://source.example.test/item/safe", results.Single(candidate => candidate.ProviderReference == "safe").InspectionUri?.ToString());
        Assert.IsNull(results.Single(candidate => candidate.ProviderReference == "unsafe").InspectionUri);
        Assert.IsNull(results.Single(candidate => candidate.ProviderReference == "credentialed").InspectionUri);
    }

    [TestMethod]
    public async Task AcquirePollsThroughToACompletedArtifact()
    {
        var client = CreateClient();

        var artifact = await client.AcquireAsync(
            _baseUrl, apiKey: null, "frankenstein", RequestMediaType.Ebook, CancellationToken.None);

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
            _baseUrl, apiKey: null, "not-a-real-candidate", RequestMediaType.Ebook, CancellationToken.None));
    }

    [TestMethod]
    public async Task SubmitAcquireWithTheSameIdempotencyKeyReturnsTheSameJob()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(Guid.NewGuid(), "frankenstein", null, null, RequestMediaType.Ebook);
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var first = await client.SubmitAcquireAsync(_baseUrl, null, request, idempotencyKey, CancellationToken.None);
        var second = await client.SubmitAcquireAsync(_baseUrl, null, request, idempotencyKey, CancellationToken.None);

        Assert.AreEqual(ProviderAcquireOutcome.Accepted, first.Outcome);
        Assert.AreEqual(first.JobId, second.JobId);
    }

    [TestMethod]
    public async Task SubmitAcquireSendsNoProviderSpecificPolicyBecauseTheProviderOwnsItsSettings()
    {
        var handler = new CapturingAcquireHandler();
        var client = new ExternalProviderClient(new RecordingHttpClientFactory(handler));
        var request = new ExternalAcquireRequest(
            Guid.NewGuid(), "candidate", null, null, RequestMediaType.Ebook);

        await client.SubmitAcquireAsync(
            "http://provider.test", null, request, Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.IsNotNull(handler.Payload);
        Assert.IsFalse(handler.Payload!.ContainsKey("acquisitionMode"));
        Assert.AreEqual("ebook", handler.Payload["mediaType"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AcquireJobReachesWaitingUserInteractionBeforeCompleting()
    {
        var client = CreateClient();
        var request = new ExternalAcquireRequest(
            Guid.NewGuid(), "the-time-machine", null, null, RequestMediaType.Ebook);

        var submission = await client.SubmitAcquireAsync(
            _baseUrl, null, request, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.AreEqual(ProviderAcquireOutcome.Accepted, submission.Outcome);

        ExternalProviderJobStatus? sawWaiting = null;
        ExternalProviderJobStatus status;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            status = await client.GetAcquireStatusAsync(
                _baseUrl, null, submission.JobId!, CancellationToken.None);
            if (status.State == ProviderAcquisitionJobLifecycleState.Waiting)
            {
                sawWaiting = status;
            }

            if (status.State is not (ProviderAcquisitionJobLifecycleState.Completed or ProviderAcquisitionJobLifecycleState.Failed))
            {
                await Task.Delay(PollInterval);
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
            _baseUrl, null, request, Guid.NewGuid().ToString("N"), CancellationToken.None);

        ExternalProviderJobStatus status;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            status = await client.GetAcquireStatusAsync(
                _baseUrl, null, submission.JobId!, CancellationToken.None);
            if (status.State != ProviderAcquisitionJobLifecycleState.Completed)
            {
                await Task.Delay(PollInterval);
            }
        }
        while (status.State != ProviderAcquisitionJobLifecycleState.Completed && DateTimeOffset.UtcNow < deadline);

        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Completed, status.State);

        var outputs = await client.ListOutputsAsync(_baseUrl, null, submission.JobId!, CancellationToken.None);
        Assert.AreEqual(2, outputs.Count);

        var primary = outputs.Single(output => output.OutputId == "primary");
        Assert.AreEqual(ProviderOutputKind.File, primary.Kind);
        Assert.IsFalse(string.IsNullOrEmpty(primary.ChecksumsJson));

        var artifact = await client.GetOutputAsync(
            _baseUrl, null, submission.JobId!, "primary", CancellationToken.None);
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
            _baseUrl, null, request, Guid.NewGuid().ToString("N"), CancellationToken.None);

        await client.CancelAcquireAsync(_baseUrl, null, submission.JobId!, CancellationToken.None);
        var status = await client.GetAcquireStatusAsync(_baseUrl, null, submission.JobId!, CancellationToken.None);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, status.State);

        await client.DeleteAcquireAsync(_baseUrl, null, submission.JobId!, CancellationToken.None);
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
            CancellationToken.None);

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
            CancellationToken.None);

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
            _baseUrl, apiKey: null, request, Guid.NewGuid().ToString("N"), CancellationToken.None);

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
                client.GetManifestAsync(securedBaseUrl, null, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(manifestWithoutKey);

            var manifest = await client.GetManifestAsync(
                securedBaseUrl, "expected-secret", CancellationToken.None);
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

    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient? CreatedClient { get; private set; }

        public HttpClient CreateClient(string name) => CreatedClient = new HttpClient(handler, disposeHandler: false);
    }

    private sealed class HoldingSearchHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[]}")
            };
        }
    }

    private sealed class CapturingSearchHandler : HttpMessageHandler
    {
        public JsonObject? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[]}")
            };
        }
    }

    private sealed class CapturingAcquireHandler : HttpMessageHandler
    {
        public JsonObject? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"jobId\":\"job-1\",\"state\":\"queued\"}")
            };
        }
    }

    private sealed class StaticSearchHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
    }
}
