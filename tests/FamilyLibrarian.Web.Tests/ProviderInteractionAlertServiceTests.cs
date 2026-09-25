using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Acquisition;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// HUMAN-ACQ-1 WP7: the alert worker's own pass logic, run directly (no hosted
/// service timer) against a real Postgres-backed host with a fake Matrix client
/// and a controllable clock, per <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c>
/// WP7's worker-tests list.
/// </summary>
[TestClass]
public sealed class ProviderInteractionAlertServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

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
    public async Task NoAlertIsSentBeforeTheSendDelayButOneIsSentAfter()
    {
        var (factory, clock, matrixClient, _) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            var room = await VerifyMatrixDestinationAsync(factory, adminId, "!admin-room:example.test");

            var (externalProviderId, jobId) = await SeedWaitingJobAsync(factory, "Delay Test Book", waitingSinceUtc: clock.UtcNow);

            await RunPassAsync(factory);
            Assert.IsFalse(
                matrixClient.Successful.Any(message => message.RoomId == room),
                "No message should be sent before the send delay elapses.");
            Assert.AreEqual(0, await CountOpenAlertsAsync(factory, externalProviderId));

            clock.UtcNow = clock.UtcNow.AddSeconds(6);
            await RunPassAsync(factory);

            var ourMessage = matrixClient.Successful.Single(message => message.RoomId == room);
            StringAssert.Contains(ourMessage.Plain, "Verify now:");
            StringAssert.Contains(ourMessage.Plain, "Delay Test Book");
            Assert.AreEqual(1, await CountOpenAlertsAsync(factory, externalProviderId));
            _ = jobId;
        }
    }

    [TestMethod]
    public async Task ThreeWaitingJobsOfOneProviderProduceExactlyOneCoalescedAlert()
    {
        var (factory, clock, matrixClient, _) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            var room = await VerifyMatrixDestinationAsync(factory, adminId, "!admin-room:example.test");

            var waitingSince = clock.UtcNow.AddSeconds(-10);
            var (externalProviderId, _) = await SeedWaitingJobAsync(factory, "First", waitingSinceUtc: waitingSince);
            await SeedWaitingJobAsync(factory, "Second", waitingSinceUtc: waitingSince, externalProviderId: externalProviderId);
            await SeedWaitingJobAsync(factory, "Third", waitingSinceUtc: waitingSince, externalProviderId: externalProviderId);

            await RunPassAsync(factory);

            Assert.AreEqual(1, await CountOpenAlertsAsync(factory, externalProviderId));
            Assert.AreEqual(
                1, matrixClient.Successful.Count(message => message.RoomId == room),
                "One provider, one admin -> exactly one message, not one per job.");
        }
    }

    [TestMethod]
    public async Task QuiescenceSuppressesANewAlertWhileTheProviderIsDrainingAndAllowsOneAfter()
    {
        var (factory, clock, matrixClient, _) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            var room = await VerifyMatrixDestinationAsync(factory, adminId, "!admin-room:example.test");

            var (externalProviderId, _) = await SeedWaitingJobAsync(
                factory, "Quiescence Test", waitingSinceUtc: clock.UtcNow.AddSeconds(-30));
            await SeedDrainedJobAsync(factory, externalProviderId, leftWaitingAtUtc: clock.UtcNow.AddSeconds(-2));

            await RunPassAsync(factory);
            Assert.IsFalse(
                matrixClient.Successful.Any(message => message.RoomId == room),
                "The provider just drained a job -- stay quiet.");

            clock.UtcNow = clock.UtcNow.AddSeconds(9); // drained job now 11s in the past; quiescence window is 10s.
            await RunPassAsync(factory);

            Assert.AreEqual(1, matrixClient.Successful.Count(message => message.RoomId == room));
        }
    }

    [TestMethod]
    public async Task ASendFailureIsRetriedOnTheNextPassWithADifferentTokenAndSucceeds()
    {
        var (factory, clock, matrixClient, tokenGenerator) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            var room = await VerifyMatrixDestinationAsync(factory, adminId, "!admin-room:example.test");
            await SeedWaitingJobAsync(factory, "Retry Test", waitingSinceUtc: clock.UtcNow.AddSeconds(-10));

            matrixClient.FailNextSend = true;
            await RunPassAsync(factory);

            Assert.IsFalse(matrixClient.Successful.Any(message => message.RoomId == room));
            Assert.AreEqual(1, matrixClient.AllAttempts.Count(attempt => attempt.RoomId == room));

            await RunPassAsync(factory);

            Assert.AreEqual(1, matrixClient.Successful.Count(message => message.RoomId == room));
            var attempts = matrixClient.AllAttempts.Where(attempt => attempt.RoomId == room).ToArray();
            Assert.AreEqual(2, attempts.Length);
            var firstLink = ExtractLink(attempts[0].Plain);
            var secondLink = ExtractLink(attempts[1].Plain);
            Assert.AreNotEqual(firstLink, secondLink, "Each attempt must use a freshly generated token.");
            _ = tokenGenerator;
        }
    }

    [TestMethod]
    public async Task ClaimingEditsEveryOtherRecipientAndCompletionEditsEveryRecipientToVerified()
    {
        var (factory, clock, matrixClient, tokenGenerator) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminAId = await GetAdminIdAsync(factory);
            var adminBId = await CreateAdminAsync(factory, "second-claim-admin");
            var roomA = await VerifyMatrixDestinationAsync(factory, adminAId, "!room-a:example.test");
            var roomB = await VerifyMatrixDestinationAsync(factory, adminBId, "!room-b:example.test");

            var (externalProviderId, jobId) = await SeedWaitingJobAsync(factory, "Claim Test", waitingSinceUtc: clock.UtcNow.AddSeconds(-10));

            await RunPassAsync(factory);
            Assert.AreEqual(2, matrixClient.Successful.Count(message => message.RoomId == roomA || message.RoomId == roomB));

            var tokenForA = await FindTokenForUserAsync(factory, tokenGenerator, adminAId, externalProviderId);
            var claimResult = await ClaimAsync(factory, tokenForA);
            Assert.AreEqual(InteractionLinkClaimOutcomeKind.Claimed, claimResult.Kind);

            await RunPassAsync(factory); // edit pass

            var editedToOther = matrixClient.Edited.Single(edit => edit.RoomId == roomB);
            StringAssert.Contains(editedToOther.Plain, "admin"); // adminA's bootstrap display name
            StringAssert.Contains(editedToOther.Plain, "handling this verification");
            var editedToSelf = matrixClient.Edited.Single(edit => edit.RoomId == roomA);
            StringAssert.Contains(editedToSelf.Plain, "You're handling this verification");

            matrixClient.Edited.Clear();
            await CompleteJobAsync(factory, jobId);
            await RunPassAsync(factory);

            var completionEdits = matrixClient.Edited.Where(edit => edit.RoomId == roomA || edit.RoomId == roomB).ToArray();
            Assert.AreEqual(2, completionEdits.Length);
            Assert.IsTrue(completionEdits.All(edit => edit.Plain.Contains("Verified", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task InAppClaimRevokesMatrixLinksAndEditsTheOpenAlert()
    {
        var (factory, clock, matrixClient, tokenGenerator) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            var room = await VerifyMatrixDestinationAsync(factory, adminId, "!in-app-room:example.test");
            var (providerId, jobId) = await SeedWaitingJobAsync(
                factory, "In-app claim", waitingSinceUtc: clock.UtcNow.AddSeconds(-10));
            await RunPassAsync(factory);
            var token = await FindTokenForUserAsync(factory, tokenGenerator, adminId, providerId);
            Assert.IsNotNull(token);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var claimService = scope.ServiceProvider.GetRequiredService<ProviderInteractionClaimService>();
                var claim = await claimService.TryClaimForUserAsync(
                    jobId, adminId, ProviderInteractionClaimChannel.InApp, null, CancellationToken.None);
                Assert.AreEqual(ClaimOutcomeKind.Claimed, claim.Kind);
            }

            Assert.AreEqual(InteractionLinkClaimOutcomeKind.ClaimedByOther,
                (await ClaimAsync(factory, token)).Kind,
                "An in-app claim must stop the still-visible Matrix link before the worker's next pass.");

            await RunPassAsync(factory);
            Assert.IsTrue(matrixClient.Edited.Any(edit => edit.RoomId == room &&
                edit.Plain.Contains("handling this verification", StringComparison.Ordinal)));
            Assert.AreEqual(InteractionLinkClaimOutcomeKind.Invalid, (await ClaimAsync(factory, token)).Kind);
        }
    }

    [TestMethod]
    public async Task TwoConcurrentMatrixRecipientsCannotBothWinDifferentJobsOfOneAlert()
    {
        var barrier = new ClaimInsertBarrier();
        var (factory, clock, _, tokenGenerator) = await CreateHarnessAsync(services =>
        {
            services.RemoveAll<IProviderInteractionClaimStore>();
            services.AddScoped<ProviderInteractionClaimStore>();
            services.AddScoped<IProviderInteractionClaimStore>(provider =>
                new BarrierClaimStore(provider.GetRequiredService<ProviderInteractionClaimStore>(), barrier));
        });
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminA = await GetAdminIdAsync(factory);
            var adminB = await CreateAdminAsync(factory, "race-admin");
            await VerifyMatrixDestinationAsync(factory, adminA, "!race-a:example.test");
            await VerifyMatrixDestinationAsync(factory, adminB, "!race-b:example.test");
            var (providerId, _) = await SeedWaitingJobAsync(
                factory, "First race job", clock.UtcNow.AddSeconds(-10));
            await SeedWaitingJobAsync(factory, "Second race job", clock.UtcNow.AddSeconds(-10), providerId);
            await RunPassAsync(factory);
            var tokenA = await FindTokenForUserAsync(factory, tokenGenerator, adminA, providerId);
            var tokenB = await FindTokenForUserAsync(factory, tokenGenerator, adminB, providerId);
            Assert.IsNotNull(tokenA);
            Assert.IsNotNull(tokenB);

            var first = ClaimAsync(factory, tokenA);
            var second = ClaimAsync(factory, tokenB);
            await barrier.WaitForBothAsync();
            barrier.Release();
            var outcomes = await Task.WhenAll(first, second);

            Assert.AreEqual(1, outcomes.Count(outcome => outcome.Kind == InteractionLinkClaimOutcomeKind.Claimed));
            Assert.AreEqual(1, outcomes.Count(outcome => outcome.Kind == InteractionLinkClaimOutcomeKind.ClaimedByOther));
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(1, await database.ProviderInteractionClaims.CountAsync(
                claim => claim.ExternalProviderId == providerId));
        }
    }

    [TestMethod]
    public async Task ALapsedClaimSupersedesTheAlertEditsMessagesAndReAlertsImmediately()
    {
        var (factory, clock, matrixClient, tokenGenerator) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            await VerifyMatrixDestinationAsync(factory, adminId, "!admin-room:example.test");
            var (externalProviderId, _) = await SeedWaitingJobAsync(factory, "Lease Test", waitingSinceUtc: clock.UtcNow.AddSeconds(-10));

            await RunPassAsync(factory);
            var firstToken = await FindTokenForUserAsync(factory, tokenGenerator, adminId, externalProviderId);
            var claimResult = await ClaimAsync(factory, firstToken);
            Assert.AreEqual(InteractionLinkClaimOutcomeKind.Claimed, claimResult.Kind);

            clock.UtcNow = clock.UtcNow.AddSeconds(6); // past the 5s claim lease, no active viewer.
            matrixClient.Edited.Clear();
            await RunPassAsync(factory); // supersede + edit + open a fresh alert, same pass.

            Assert.IsTrue(matrixClient.Edited.Any(edit => edit.Plain.Contains("expired", StringComparison.Ordinal)));

            var secondToken = await FindTokenForUserAsync(factory, tokenGenerator, adminId, externalProviderId, excluding: firstToken);
            Assert.IsNotNull(secondToken);
            Assert.AreNotEqual(firstToken, secondToken);
        }
    }

    [TestMethod]
    public async Task QuietHoursHoldsOneAdminsMessageWhileTheOtherIsSentAndSkipsItOnceResolved()
    {
        var (factory, clock, matrixClient, _) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            // Both freshly created, never the shared bootstrap admin: quiet hours
            // are persisted directly on the AppUser row, and that row survives
            // this test -- setting it on the bootstrap admin would leak into
            // every later test method in this class that reuses it.
            var quietAdminId = await CreateAdminAsync(factory, "quiet-admin");
            var awakeAdminId = await CreateAdminAsync(factory, "awake-admin");
            var quietRoom = await VerifyMatrixDestinationAsync(factory, quietAdminId, "!quiet-room:example.test");
            var awakeRoom = await VerifyMatrixDestinationAsync(factory, awakeAdminId, "!awake-room:example.test");
            await SetQuietHoursCoveringNowAsync(factory, quietAdminId, clock.UtcNow);

            var (externalProviderId, jobId) = await SeedWaitingJobAsync(
                factory, "Quiet Hours Test", waitingSinceUtc: clock.UtcNow.AddSeconds(-10));

            await RunPassAsync(factory);

            // Filtered by room, not a raw count: the bootstrap admin's row is
            // shared across every test method in this class, and an earlier test
            // may have left another verified admin behind in the same database.
            Assert.IsTrue(matrixClient.Successful.Any(message => message.RoomId == awakeRoom));
            Assert.IsFalse(matrixClient.Successful.Any(message => message.RoomId == quietRoom));

            await CompleteJobAsync(factory, jobId);
            await RunPassAsync(factory);

            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alert = await database.ProviderInteractionAlerts
                .Include(candidate => candidate.Recipients)
                .Where(candidate => candidate.ExternalProviderId == externalProviderId)
                .OrderByDescending(candidate => candidate.CreatedAtUtc)
                .FirstAsync();
            var quietRecipient = alert.Recipients.Single(recipient => recipient.UserId == quietAdminId);
            Assert.AreEqual(ProviderInteractionRecipientDeliveryState.Skipped, quietRecipient.DeliveryState);
        }
    }

    [TestMethod]
    public async Task NoCapturedLogLineEverContainsTheTokenOrTheInteractionFragment()
    {
        var (factory, clock, matrixClient, tokenGenerator) = await CreateHarnessAsync();
        await using (factory)
        {
            await EnableMatrixAsync(factory);
            var adminId = await GetAdminIdAsync(factory);
            var room = await VerifyMatrixDestinationAsync(factory, adminId, "!admin-room:example.test");
            await SeedWaitingJobAsync(factory, "Log Safety Test", waitingSinceUtc: clock.UtcNow.AddSeconds(-10));

            var capturing = new CapturingLoggerProvider();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                loggerFactory.AddProvider(capturing);
                var alertService = scope.ServiceProvider.GetRequiredService<ProviderInteractionAlertService>();
                await alertService.RunPassAsync(CancellationToken.None);
            }

            // Filtered by room, not a raw count -- see the quiet-hours test's
            // comment for why the shared database can hold other tests' admins.
            var ourMessage = matrixClient.Successful.Single(message => message.RoomId == room);
            var token = ExtractToken(ourMessage.Plain);
            Assert.IsFalse(string.IsNullOrEmpty(token));

            foreach (var line in capturing.Lines)
            {
                StringAssert.DoesNotMatch(line, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(token!)));
                StringAssert.DoesNotMatch(line, new System.Text.RegularExpressions.Regex("#[A-Za-z0-9_-]{20,}"));
            }

            _ = tokenGenerator;
        }
    }

    // ----- Harness -----

    private static async Task<(FamilyLibrarianAppFactory Factory, FakeClock Clock, RecordingMatrixClient MatrixClient, RecordingTokenGenerator TokenGenerator)>
        CreateHarnessAsync(Action<IServiceCollection>? configure = null)
    {
        var fixture = WebTestFixture.Require(_fixture);
        var clock = new FakeClock { UtcNow = Epoch };
        var matrixClient = new RecordingMatrixClient();
        RecordingTokenGenerator? tokenGenerator = null;

        var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<IMatrixClient>();
            services.AddSingleton<IMatrixClient>(matrixClient);
            services.RemoveAll<ProviderInteractionAlertOptions>();
            services.AddSingleton(new ProviderInteractionAlertOptions
            {
                PublicOrigin = "https://fl.example.test",
                SendDelaySeconds = 5,
                QuiescenceSeconds = 10,
                ClaimLeaseSeconds = 5,
                AlertMaxAgeHours = 24,
                PassIntervalSeconds = 15,
                MaxSendAttempts = 3
            });

            // Wrap, not replace: the real token generator's crypto is still
            // exercised, this just records each plaintext token as it's minted so
            // tests can locate "the token that went to admin X" after the fact.
            services.RemoveAll<ISecureTokenGenerator>();
            services.AddSingleton<ISecureTokenGenerator>(provider =>
            {
                var inner = provider.GetRequiredService<InvitationTokenGenerator>();
                tokenGenerator = new RecordingTokenGenerator(inner);
                return tokenGenerator;
            });
            configure?.Invoke(services);
        });

        // Force the wrapped registration above to resolve once, so
        // `tokenGenerator` is non-null by the time the test uses it.
        await using (var warmup = factory.Services.CreateAsyncScope())
        {
            warmup.ServiceProvider.GetRequiredService<ISecureTokenGenerator>();
        }

        return (factory, clock, matrixClient, tokenGenerator!);
    }

    private sealed class ClaimInsertBarrier
    {
        private readonly TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothArrived.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        }

        public Task WaitForBothAsync() => bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => release.TrySetResult();
    }

    private sealed class BarrierClaimStore(ProviderInteractionClaimStore inner, ClaimInsertBarrier barrier)
        : IProviderInteractionClaimStore
    {
        public Task<ProviderInteractionClaim?> FindAsync(Guid jobId, CancellationToken cancellationToken) =>
            inner.FindAsync(jobId, cancellationToken);

        public async Task<bool> TryInsertAsync(ProviderInteractionClaim claim, CancellationToken cancellationToken)
        {
            var inserted = await inner.TryInsertAsync(claim, cancellationToken);
            if (inserted)
            {
                await barrier.ArriveAsync(cancellationToken);
            }

            return inserted;
        }

        public Task ReleaseIfOwnedAsync(Guid jobId, Guid userId, Guid alertId, CancellationToken cancellationToken) =>
            inner.ReleaseIfOwnedAsync(jobId, userId, alertId, cancellationToken);

        public void Remove(ProviderInteractionClaim claim) => inner.Remove(claim);

        public Task<IReadOnlyList<ProviderInteractionClaim>> ListAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);
    }

    private static async Task RunPassAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var alertService = scope.ServiceProvider.GetRequiredService<ProviderInteractionAlertService>();
        await alertService.RunPassAsync(CancellationToken.None);
    }

    private static async Task<InteractionLinkClaimResult> ClaimAsync(FamilyLibrarianAppFactory factory, string? token)
    {
        Assert.IsNotNull(token);
        await using var scope = factory.Services.CreateAsyncScope();
        var linkService = scope.ServiceProvider.GetRequiredService<ProviderInteractionLinkService>();
        return await linkService.ClaimAsync(token, CancellationToken.None);
    }

    private static async Task EnableMatrixAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
        var settings = new MatrixSettings(Epoch);
        settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, Epoch);
        settings.SetAccessToken(
            protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "test-access-token"),
            protector.FormatVersion, actorUserId: null, Epoch);
        settings.SetEnabled(true, actorUserId: null, Epoch);
        database.MatrixSettings.Add(settings);
        await database.SaveChangesAsync();
    }

    private static async Task<Guid> GetAdminIdAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);
        return admin.Id;
    }

    private static async Task<Guid> CreateAdminAsync(FamilyLibrarianAppFactory factory, string localPart)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"{localPart}-{Guid.NewGuid():N}@family-librarian.example";
        var user = new AppUser { UserName = email, Email = email, DisplayName = localPart, EmailConfirmed = true };
        Assert.IsTrue((await users.CreateAsync(user, WebTestFixture.UserPassword)).Succeeded);
        Assert.IsTrue((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        return user.Id;
    }

    /// <summary>
    /// Verifies (or re-verifies) <paramref name="userId"/>'s Matrix destination.
    /// Upserts rather than always inserting, and always mints a fresh globally-unique
    /// room id: the bootstrap admin is the same row across every test method in this
    /// class (one shared database per class, per <see cref="WebTestFixture"/>), and
    /// both <c>user_id</c> and <c>room_id</c> are unique-constrained.
    /// </summary>
    private static async Task<string> VerifyMatrixDestinationAsync(FamilyLibrarianAppFactory factory, Guid userId, string roomIdPrefix)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roomId = $"{roomIdPrefix}-{Guid.NewGuid():N}";
        var destination = await database.UserMatrixDestinations.SingleOrDefaultAsync(candidate => candidate.UserId == userId);
        if (destination is null)
        {
            destination = new UserMatrixDestination(userId, Epoch);
            database.UserMatrixDestinations.Add(destination);
        }

        destination.RequestVerification($"@{userId:N}:example.test", roomId, "000000", Epoch);
        destination.Verify(Epoch);
        await database.SaveChangesAsync();
        return roomId;
    }

    private static async Task SetQuietHoursCoveringNowAsync(FamilyLibrarianAppFactory factory, Guid userId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await database.Users.SingleAsync(candidate => candidate.Id == userId);
        var minuteOfDay = (now.UtcDateTime.Hour * 60) + now.UtcDateTime.Minute;
        user.QuietHoursTimeZoneId = "UTC";
        user.QuietHoursStartMinute = Math.Max(0, minuteOfDay - 30);
        user.QuietHoursEndMinute = Math.Min(1439, minuteOfDay + 30);
        await database.SaveChangesAsync();
    }

    private static async Task<(Guid ExternalProviderId, Guid JobId)> SeedWaitingJobAsync(
        FamilyLibrarianAppFactory factory, string workTitle, DateTimeOffset waitingSinceUtc, Guid? externalProviderId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);

        ExternalProvider provider;
        if (externalProviderId is { } existingId)
        {
            provider = await database.ExternalProviders.SingleAsync(candidate => candidate.Id == existingId);
        }
        else
        {
            var providerId = $"alert-worker-test-provider-{Guid.NewGuid():N}";
            provider = new ExternalProvider(providerId, "Alert Worker Test Provider", "http://alert-worker-test-provider.invalid", waitingSinceUtc);
            database.ExternalProviders.Add(provider);
        }

        var work = new Work(workTitle, null, null, null, PublicationStatus.Published, waitingSinceUtc);
        database.Works.Add(work);
        var request = new BookRequest(admin.Id, work.Id, [RequestMediaType.Ebook], null, waitingSinceUtc);
        database.BookRequests.Add(request);
        var format = request.Formats.Single();

        var job = new ProviderAcquisitionJob(
            request.Id, format.Id, provider.Id, provider.ProviderId, null,
            Guid.NewGuid().ToString("N"), "candidate-ref", null, null, waitingSinceUtc);
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, waitingSinceUtc, waitingSinceUtc);
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
            interactionType: "browser", interactionMessage: "solve the check",
            interactionExpiresAtUtc: waitingSinceUtc.AddHours(1), interactionResumeSupported: true,
            interactionActionUrl: null, progressPercent: null, progressBytesCompleted: null,
            progressBytesTotal: null, progressMessage: null, nextPollAtUtc: waitingSinceUtc.AddSeconds(30),
            atUtc: waitingSinceUtc);
        database.ProviderAcquisitionJobs.Add(job);

        await database.SaveChangesAsync();
        return (provider.Id, job.Id);
    }

    /// <summary>A second job of the same provider that already left Waiting -- feeds the quiescence check.</summary>
    private static async Task SeedDrainedJobAsync(
        FamilyLibrarianAppFactory factory, Guid externalProviderId, DateTimeOffset leftWaitingAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);
        var provider = await database.ExternalProviders.SingleAsync(candidate => candidate.Id == externalProviderId);

        var work = new Work("Drained Job", null, null, null, PublicationStatus.Published, leftWaitingAtUtc);
        database.Works.Add(work);
        var request = new BookRequest(admin.Id, work.Id, [RequestMediaType.Ebook], null, leftWaitingAtUtc);
        database.BookRequests.Add(request);
        var format = request.Formats.Single();

        var job = new ProviderAcquisitionJob(
            request.Id, format.Id, provider.Id, provider.ProviderId, null,
            Guid.NewGuid().ToString("N"), "candidate-ref", null, null, leftWaitingAtUtc.AddMinutes(-5));
        job.RecordSubmission(
            "provider-job-drained", ProviderAcquisitionJobLifecycleState.Waiting,
            leftWaitingAtUtc.AddMinutes(-5), leftWaitingAtUtc.AddMinutes(-5));
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Completed, null, null, null, null, null, null,
            null, null, null, null, nextPollAtUtc: null, atUtc: leftWaitingAtUtc);
        database.ProviderAcquisitionJobs.Add(job);

        await database.SaveChangesAsync();
    }

    private static async Task CompleteJobAsync(FamilyLibrarianAppFactory factory, Guid jobId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var job = await database.ProviderAcquisitionJobs.SingleAsync(candidate => candidate.Id == jobId);
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Completed, null, null, null, null, null, null,
            null, null, null, null, nextPollAtUtc: null, atUtc: clock.UtcNow);
        await database.SaveChangesAsync();
    }

    private static async Task<int> CountOpenAlertsAsync(FamilyLibrarianAppFactory factory, Guid externalProviderId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.ProviderInteractionAlerts.CountAsync(alert =>
            alert.ExternalProviderId == externalProviderId &&
            (alert.State == ProviderInteractionAlertState.Open || alert.State == ProviderInteractionAlertState.Claimed));
    }

    /// <summary>
    /// Finds, among the tokens minted <em>by this test's own</em> <see cref="RecordingTokenGenerator"/>
    /// instance, the one whose hash matches a recipient row for <paramref name="userId"/>
    /// on the given provider's alert. Scoped to <paramref name="externalProviderId"/> and to
    /// this test's own generator (never the shared database globally) because the bootstrap
    /// admin and its recipient history are shared across every test method in this class.
    /// </summary>
    private static async Task<string?> FindTokenForUserAsync(
        FamilyLibrarianAppFactory factory, RecordingTokenGenerator tokenGenerator, Guid userId,
        Guid externalProviderId, string? excluding = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hashes = await database.ProviderInteractionAlerts
            .Where(alert => alert.ExternalProviderId == externalProviderId)
            .SelectMany(alert => alert.Recipients)
            .Where(recipient => recipient.UserId == userId && recipient.TokenHash != null)
            .Select(recipient => recipient.TokenHash!)
            .ToArrayAsync();

        foreach (var candidateToken in tokenGenerator.CreatedTokens.AsEnumerable().Reverse())
        {
            if (candidateToken == excluding)
            {
                continue;
            }

            if (hashes.Contains(tokenGenerator.Hash(candidateToken)))
            {
                return candidateToken;
            }
        }

        return null;
    }

    private static string? ExtractLink(string plainBody)
    {
        var index = plainBody.IndexOf("Verify now: ", StringComparison.Ordinal);
        return index < 0 ? null : plainBody[(index + "Verify now: ".Length)..];
    }

    private static string? ExtractToken(string plainBody)
    {
        var link = ExtractLink(plainBody);
        var hashIndex = link?.IndexOf('#') ?? -1;
        return hashIndex < 0 ? null : link![(hashIndex + 1)..];
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class RecordingTokenGenerator(ISecureTokenGenerator inner) : ISecureTokenGenerator
    {
        public List<string> CreatedTokens { get; } = [];

        public string CreateToken()
        {
            var token = inner.CreateToken();
            CreatedTokens.Add(token);
            return token;
        }

        public string Hash(string token) => inner.Hash(token);
    }

    private sealed class RecordingMatrixClient : IMatrixClient
    {
        private int _eventCounter;

        public bool FailNextSend { get; set; }

        public List<(string RoomId, string Plain, string Html, string EventId)> Successful { get; } = [];

        public List<(string RoomId, string Plain, string Html)> AllAttempts { get; } = [];

        public List<(string RoomId, string EventId, string Plain, string Html)> Edited { get; } = [];

        public Task<ConnectionTestOutcome> TestConnectionAsync(
            MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
            MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SendResult> SendMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixSyncResult> SyncAsync(
            MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MatrixSendResult> SendRichMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody,
            CancellationToken cancellationToken)
        {
            AllAttempts.Add((roomId, plainBody, htmlBody));
            if (FailNextSend)
            {
                FailNextSend = false;
                return Task.FromResult(MatrixSendResult.Failure("simulated failure"));
            }

            var eventId = $"$event{++_eventCounter}";
            Successful.Add((roomId, plainBody, htmlBody, eventId));
            return Task.FromResult(MatrixSendResult.Success(eventId));
        }

        public Task<SendResult> EditMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody,
            string htmlBody, CancellationToken cancellationToken)
        {
            Edited.Add((roomId, eventId, plainBody, htmlBody));
            return Task.FromResult(SendResult.Success());
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Lines.Add(formatter(state, exception));
            }
        }
    }
}
