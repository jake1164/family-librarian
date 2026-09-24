using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// HUMAN-ACQ-1 Phase 3: proves the actual byte-relay endpoint through the
/// real host -- real cookie authentication, real admin-role check, the real
/// <see cref="ProviderRemoteViewBrokerService"/> pump loop -- with only the
/// outbound provider-facing leg faked (<see cref="IProviderRemoteViewClient"/>
/// is the seam this codebase already uses for the equivalent HTTP client,
/// see <c>AlwaysEmptyExternalProviderClient</c>). No real provider process is
/// started; that end-to-end proof is Phase 4's job.
/// </summary>
[TestClass]
public sealed class ProviderInteractionViewEndpointTests
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
    public async Task BytesRelayInBothDirectionsBetweenTheAdminAndTheFakeProvider()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var providerSide = new FakeProviderRemoteViewConnection();
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IProviderRemoteViewClient>();
                services.AddSingleton<IProviderRemoteViewClient>(new FakeProviderRemoteViewClient(providerSide));
            });

        var jobId = await SeedWaitingJobWithViewSessionAsync(factory, expiresInMinutes: 10);

        using var browserSocket = await ConnectAsAdminAsync(factory, fixture, jobId);
        Assert.AreEqual(WebSocketState.Open, browserSocket.State);

        // Admin -> provider.
        await browserSocket.SendAsync(
            Encoding.UTF8.GetBytes("hello-provider"), WebSocketMessageType.Binary, true, CancellationToken.None);
        var seenByProvider = await providerSide.ReceiveFromBrowserAsync(CancellationToken.None);
        Assert.AreEqual("hello-provider", Encoding.UTF8.GetString(seenByProvider));

        // Provider -> admin.
        providerSide.SendToBrowser(Encoding.UTF8.GetBytes("hello-admin"));
        var receiveBuffer = new byte[1024];
        var received = await browserSocket.ReceiveAsync(receiveBuffer, CancellationToken.None);
        Assert.AreEqual("hello-admin", Encoding.UTF8.GetString(receiveBuffer, 0, received.Count));

        providerSide.SimulateProviderClose();
        var closeFrame = await browserSocket.ReceiveAsync(receiveBuffer, CancellationToken.None);
        Assert.AreEqual(WebSocketMessageType.Close, closeFrame.MessageType);
    }

    [TestMethod]
    public async Task ASecondConcurrentViewAttemptForTheSameJobIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IProviderRemoteViewClient>();
                services.AddSingleton<IProviderRemoteViewClient>(
                    new FakeProviderRemoteViewClient(new FakeProviderRemoteViewConnection()));
            });

        var jobId = await SeedWaitingJobWithViewSessionAsync(factory, expiresInMinutes: 10);

        using var first = await ConnectAsAdminAsync(factory, fixture, jobId);
        Assert.AreEqual(WebSocketState.Open, first.State);

        using var second = await ConnectAsAdminAsync(factory, fixture, jobId);
        var buffer = new byte[16];
        var result = await second.ReceiveAsync(buffer, CancellationToken.None);
        Assert.AreEqual(WebSocketMessageType.Close, result.MessageType);
        Assert.AreEqual(WebSocketCloseStatus.PolicyViolation, second.CloseStatus);
    }

    [TestMethod]
    public async Task AJobThatIsNotWaitingIsRejectedBeforeTheUpgrade()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IProviderRemoteViewClient>();
                services.AddSingleton<IProviderRemoteViewClient>(
                    new FakeProviderRemoteViewClient(new FakeProviderRemoteViewConnection()));
            });

        var jobId = await SeedWaitingJobWithViewSessionAsync(factory, expiresInMinutes: 10, startInteractionView: false);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ConnectAsAdminAsync(factory, fixture, jobId));
        StringAssert.Contains(exception.Message, "409");
    }

    [TestMethod]
    public async Task AnExpiredInteractionIsRejectedBeforeTheUpgrade()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IProviderRemoteViewClient>();
                services.AddSingleton<IProviderRemoteViewClient>(
                    new FakeProviderRemoteViewClient(new FakeProviderRemoteViewConnection()));
            });

        var jobId = await SeedWaitingJobWithViewSessionAsync(factory, expiresInMinutes: -1);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ConnectAsAdminAsync(factory, fixture, jobId));
        StringAssert.Contains(exception.Message, "409");
    }

    [TestMethod]
    public async Task AnUnauthenticatedCallerIsRejectedBeforeTheUpgrade()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IProviderRemoteViewClient>();
                services.AddSingleton<IProviderRemoteViewClient>(
                    new FakeProviderRemoteViewClient(new FakeProviderRemoteViewConnection()));
            });

        var jobId = await SeedWaitingJobWithViewSessionAsync(factory, expiresInMinutes: 10);

        using var anonymous = fixture.CreateAnonymousClient();
        var response = await anonymous.GetAsync($"/api/v1/admin/requests/provider-interactions/{jobId}/view");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WebSocket> ConnectAsAdminAsync(
        FamilyLibrarianAppFactory factory, WebTestFixture fixture, Guid jobId)
    {
        var cookie = await fixture.IssueIdentityCookieAsync(
            FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
        return await client.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/api/v1/admin/requests/provider-interactions/{jobId}/view"),
            CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the expected condition.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Builds a real request/format/provider/job graph directly against the
    /// database -- a full <c>/acquire</c> submission round trip isn't needed
    /// to test the view endpoint, only a job already sitting in
    /// <c>Waiting</c> with an interaction session recorded, exactly as
    /// <see cref="ProviderInteractionService.StartAsync"/> leaves one.
    /// </summary>
    private static async Task<Guid> SeedWaitingJobWithViewSessionAsync(
        FamilyLibrarianAppFactory factory, int expiresInMinutes, bool startInteractionView = true)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);

        var work = new Work("A Brokered View Test Book", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);

        var request = new BookRequest(admin.Id, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        var format = request.Formats.Single();

        var providerId = $"view-test-provider-{Guid.NewGuid():N}";
        var provider = new ExternalProvider(providerId, "View Test Provider", "http://view-test-provider.invalid", now);
        provider.RecordTestResult(
            succeeded: true, message: null, protocolVersion: "2",
            capabilities: "mediaTypes:ebook;operations:search,acquire;features:waiting-interaction-control,interaction-view",
            egressPolicy: EgressPolicy.Normal, actorUserId: null, testedAtUtc: now, manifestReached: true);
        database.ExternalProviders.Add(provider);

        var job = new ProviderAcquisitionJob(
            request.Id, format.Id, provider.Id, provider.ProviderId, null,
            Guid.NewGuid().ToString("N"), "candidate-ref", null, null, now);
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, now, now);
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
            interactionType: "browser", interactionMessage: "solve the check",
            interactionExpiresAtUtc: now.AddMinutes(expiresInMinutes), interactionResumeSupported: true,
            interactionActionUrl: null, progressPercent: null, progressBytesCompleted: null,
            progressBytesTotal: null, progressMessage: null, nextPollAtUtc: now.AddSeconds(30), atUtc: now);
        if (startInteractionView)
        {
            job.RecordInteractionSessionStarted(now);
        }

        database.ProviderAcquisitionJobs.Add(job);
        await database.SaveChangesAsync();
        return job.Id;
    }

    private sealed class FakeProviderRemoteViewClient(FakeProviderRemoteViewConnection connection) : IProviderRemoteViewClient
    {
        public Task<IProviderRemoteViewConnection> ConnectAsync(
            string baseUrl, string? apiKey, string providerJobId, EgressRoute route, CancellationToken cancellationToken) =>
            Task.FromResult<IProviderRemoteViewConnection>(connection);
    }

    private sealed class FakeProviderRemoteViewConnection : IProviderRemoteViewConnection
    {
        private readonly Channel<byte[]> _toProvider = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _toBrowser =
            Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();

        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public Task SendAsync(ReadOnlyMemory<byte> buffer, bool endOfMessage, CancellationToken cancellationToken)
        {
            _toProvider.Writer.TryWrite(buffer.ToArray());
            return Task.CompletedTask;
        }

        public async Task<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var (data, type) = await _toBrowser.Reader.ReadAsync(cancellationToken);
            data.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(data.Length, type, endOfMessage: true);
        }

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> ReceiveFromBrowserAsync(CancellationToken cancellationToken) =>
            await _toProvider.Reader.ReadAsync(cancellationToken);

        public void SendToBrowser(byte[] data) => _toBrowser.Writer.TryWrite((data, WebSocketMessageType.Binary));

        public void SimulateProviderClose() => _toBrowser.Writer.TryWrite(([], WebSocketMessageType.Close));
    }
}
