using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.Setup;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Contracts.Setup;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Setup;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// First-time setup when things collide or go missing: competing saves, features that are not set up or are turned
/// off, and the other ways an account can first appear.
/// </summary>
[TestClass]
public sealed class SetupResilienceTests
{
    private const string Mine = "/api/v1/me/setup/";
    private const string Guidance = "/api/v1/admin/setup-guidance/";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TwoFirstSavesThatBothSeeNoRowLeaveOneRowAndTheLoserIsTold()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var first = fixture.Services.CreateAsyncScope();
        await using var second = fixture.Services.CreateAsyncScope();
        var one = first.ServiceProvider.GetRequiredService<ISetupGuidanceStore>();
        var two = second.ServiceProvider.GetRequiredService<ISetupGuidanceStore>();

        // Both read before either writes, which is the timing that used to make two rows.
        var created = await one.GetOrCreateAsync(CancellationToken.None);
        var competing = await two.GetOrCreateAsync(CancellationToken.None);
        Assert.IsNull(created.TrySetKindle("library@example.org", null, null, Now));
        Assert.IsNull(competing.TrySetMessaging("chat.example.org", null, null, null, null, Now));

        Assert.IsTrue(await one.TrySaveChangesAsync(CancellationToken.None));
        Assert.IsFalse(await two.TrySaveChangesAsync(CancellationToken.None));

        // Reading again and reapplying keeps both.
        var reread = await two.GetOrCreateAsync(CancellationToken.None);
        Assert.AreEqual("library@example.org", reread.KindleSenderAddress);
        Assert.IsNull(reread.TrySetMessaging("chat.example.org", null, null, null, null, Now));
        Assert.IsTrue(await two.TrySaveChangesAsync(CancellationToken.None));

        await AssertOneRowAsync(fixture, "library@example.org", "chat.example.org");
    }

    [TestMethod]
    public async Task AChangeToAnOutOfDateRowIsRefusedRatherThanThrown()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await WithTokenAsync(await fixture.CreateAdminClientAsync());
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"{Guidance}kindle", new SetKindleGuidanceRequest("library@example.org", null))).StatusCode);
        await using var first = fixture.Services.CreateAsyncScope();
        await using var second = fixture.Services.CreateAsyncScope();
        var one = first.ServiceProvider.GetRequiredService<ISetupGuidanceStore>();
        var two = second.ServiceProvider.GetRequiredService<ISetupGuidanceStore>();
        var mine = await one.GetOrCreateAsync(CancellationToken.None);
        var stale = await two.GetOrCreateAsync(CancellationToken.None);

        mine.SetShowElementHelp(false, null, Now);
        Assert.IsTrue(await one.TrySaveChangesAsync(CancellationToken.None));
        Assert.IsNull(stale.TrySetMessaging("chat.example.org", null, null, null, null, Now));

        Assert.IsFalse(await two.TrySaveChangesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ASaveThatLosesTheRaceIsReappliedSoNeitherCardIsLost()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        // The other card is saved in the gap between this save reading the row and writing it.
        var interrupted = new InterruptedStore(services.GetRequiredService<ISetupGuidanceStore>(), async () =>
        {
            await using var other = fixture.Services.CreateAsyncScope();
            var store = other.ServiceProvider.GetRequiredService<ISetupGuidanceStore>();
            var row = await store.GetOrCreateAsync(CancellationToken.None);
            Assert.IsNull(row.TrySetKindle("library@example.org", null, null, Now));
            Assert.IsTrue(await store.TrySaveChangesAsync(CancellationToken.None));
        });
        var service = new SetupGuidanceService(
            interrupted, services.GetRequiredService<IMatrixSettingsStore>(),
            services.GetRequiredService<IAuditWriter>(), services.GetRequiredService<ICurrentUser>(),
            services.GetRequiredService<IClock>());

        var result = await service.SetMessagingAsync("chat.example.org", null, null, null, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual("library@example.org", result.Status!.KindleSenderAddress);
        await AssertOneRowAsync(fixture, "library@example.org", "chat.example.org");
    }

    [TestMethod]
    public async Task BothCardsSavedAtOnceOverHttpAreBothKept()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await WithTokenAsync(await fixture.CreateAdminClientAsync());

        for (var round = 0; round < 5; round++)
        {
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().SetupGuidanceSettings.ExecuteDeleteAsync();
            }

            var responses = await Task.WhenAll(
                admin.PutAsJsonAsync($"{Guidance}kindle", new SetKindleGuidanceRequest("library@example.org", null)),
                admin.PutAsJsonAsync($"{Guidance}messaging",
                    new SetMessagingGuidanceRequest("chat.example.org", null, null, null)));

            Assert.IsTrue(responses.All(response => response.StatusCode == HttpStatusCode.OK),
                string.Join(", ", responses.Select(response => response.StatusCode)));
            await AssertOneRowAsync(fixture, "library@example.org", "chat.example.org");
        }
    }

    [TestMethod]
    public async Task CompetingPreferenceAndProgressWritesNeverReportASaveThatWasLost()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var user = await WithTokenAsync(await fixture.CreateUserClientAsync());
        const string format = "/api/v1/me/default-request-format/";

        for (var round = 0; round < 10; round++)
        {
            var wanted = round % 2 == 0 ? "Both" : "Audiobook";
            var preference = user.PutAsJsonAsync(format, new SetDefaultRequestFormatRequest(wanted));
            var progress = user.PostAsync($"{Mine}steps/formats/completed", null);
            await Task.WhenAll(preference, progress);

            // Losing a race is allowed and is reported; a server error or a silently dropped save is not.
            foreach (var response in new[] { await preference, await progress })
            {
                Assert.IsTrue((int)response.StatusCode < 500, response.StatusCode.ToString());
            }

            if ((await preference).IsSuccessStatusCode)
            {
                Assert.AreEqual(wanted, (await user.GetFromJsonAsync<DefaultRequestFormatResponse>(format))!.Format);
            }

            if ((await progress).IsSuccessStatusCode)
            {
                Assert.IsTrue(Step(await GetMineAsync(user), SetupStepNames.Formats).IsCompleted);
            }
        }
    }

    [TestMethod]
    public async Task WithNothingSetUpThereIsNothingToOfferAndEachFeatureAppearsAndLeavesOnItsOwn()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var scanner = new CountingGuard();
        // The real readiness service, so that what is offered follows the saved switches.
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IFormatReadinessService>();
            services.AddScoped<IFormatReadinessService, FormatReadinessService>();
            services.RemoveAll<IAcquisitionBoundaryGuard>();
            services.AddSingleton<IAcquisitionBoundaryGuard>(scanner);
        });
        using var user = await SignInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

        var nothing = await GetMineAsync(user);
        Assert.IsTrue(nothing.Steps.All(step => !step.IsAvailable));
        Assert.IsFalse(nothing.ShouldOpenWizard);
        Assert.IsEmpty(nothing.ReminderSteps);

        await ChangeAsync(factory, async (database, _) =>
        {
            var audiobookshelf = new AudiobookshelfSettings(Now);
            audiobookshelf.SetEnabled(true, null, Now);
            database.AudiobookshelfSettings.Add(audiobookshelf);
            await database.SaveChangesAsync();
        });
        CollectionAssert.AreEqual(new[] { SetupStepNames.Formats }, Available(await GetMineAsync(user)));
        Assert.AreEqual(0, scanner.Calls, "Asking what to set up must not reach the malware scanner.");

        await ChangeAsync(factory, async (database, protector) =>
        {
            var audiobookshelf = await database.AudiobookshelfSettings.SingleAsync();
            audiobookshelf.SetEnabled(false, null, Now);
            database.CwaSettings.Add(KindleReadyCwa(protector));
            await database.SaveChangesAsync();
        });
        CollectionAssert.AreEqual(new[] { SetupStepNames.Kindle }, Available(await GetMineAsync(user)));

        await ChangeAsync(factory, async (database, protector) =>
        {
            await database.CwaSettings.ExecuteDeleteAsync();
            database.MatrixSettings.Add(EnabledMatrix(protector));
            await database.SaveChangesAsync();
        });
        var messagingOnly = await GetMineAsync(user);
        CollectionAssert.AreEqual(new[] { SetupStepNames.Messaging }, Available(messagingOnly));
        Assert.IsNull(messagingOnly.Kindle);

        // Turned off again: the step and its guidance go with it.
        await ChangeAsync(factory, async (database, _) =>
        {
            (await database.MatrixSettings.SingleAsync()).SetEnabled(false, null, Now);
            await database.SaveChangesAsync();
        });
        var off = await GetMineAsync(user);
        Assert.IsEmpty(Available(off));
        Assert.IsNull(off.Messaging);
        Assert.AreEqual(0, scanner.Calls);
    }

    [TestMethod]
    public async Task AnInvitedMemberIsNewAndGetsTheWizard()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await WithTokenAsync(await fixture.CreateAdminClientAsync());
        const string email = "invited@family-librarian.example";
        const string password = "Invited-Member-Pass1!";
        var invited = await admin.PostAsJsonAsync("/api/v1/admin/invitations/", new CreateInvitationRequest(email, false));
        var invitation = await invited.Content.ReadFromJsonAsync<CreatedInvitationResponse>();
        using var anonymous = fixture.CreateAnonymousClient();
        var redeemed = await anonymous.PostAsJsonAsync(
            "/api/v1/invitations/redeem", new RedeemInvitationRequest(invitation!.Token, "Invited", password));
        Assert.AreEqual(HttpStatusCode.NoContent, redeemed.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password })).StatusCode);

        var setup = await GetMineAsync(anonymous);

        Assert.IsFalse(setup.WizardSeen);
        Assert.IsTrue(setup.ShouldOpenWizard);
    }

    [TestMethod]
    public async Task AnAccountCreatedBySingleSignOnIsNewAndGetsTheWizard()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ExternalSignInService>().SignInAsync(
            new ExternalIdentity(
                "https://sign-in.example.test/", "subject-1", "sso@family-librarian.example", "Sso Member", false),
            CancellationToken.None);
        Assert.AreEqual(ExternalSignInOutcome.SignedIn, result.Outcome, result.Message);

        var state = await scope.ServiceProvider.GetRequiredService<IUserSetupStateStore>()
            .GetAsync(result.UserId!.Value, CancellationToken.None);

        Assert.AreEqual(new UserSetupState(null, SetupSteps.None, SetupSteps.None), state);
    }

    [TestMethod]
    public async Task TheCodeLimitAnswersTooManyRequestsWithTheWaitAndOnlyForThatMember()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var homeserver = new StandInMatrixClient();
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IMatrixClient>();
            services.AddSingleton<IMatrixClient>(homeserver);
        });
        await ChangeAsync(factory, async (database, protector) =>
        {
            database.MatrixSettings.Add(EnabledMatrix(protector));
            await database.SaveChangesAsync();
        });
        using var user = await SignInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        user.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(user));
        const string link = "/api/v1/me/communications/matrix/";
        for (var send = 0; send < MatrixCodeSendLimiter.MaxSends; send++)
        {
            Assert.AreEqual(HttpStatusCode.OK,
                (await user.PostAsJsonAsync(link, new RequestMatrixLinkRequest("reader"))).StatusCode);
        }

        // Each request is a new scope, so this also shows the count outlives the request.
        var refused = await user.PostAsJsonAsync(link, new RequestMatrixLinkRequest("someone"));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.IsTrue(refused.Headers.RetryAfter?.Delta is { } wait &&
            wait > TimeSpan.FromMinutes(14) && wait <= MatrixCodeSendLimiter.Window, refused.Headers.ToString());
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "which is the limit");
        Assert.AreEqual(MatrixCodeSendLimiter.MaxSends, homeserver.Invitations);

        using var admin = await SignInAsync(
            factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        Assert.AreEqual(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync(link, new RequestMatrixLinkRequest("admin"))).StatusCode);
    }

    private static async Task AssertOneRowAsync(WebTestFixture fixture, string sender, string serverName)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SetupGuidanceSettings.AsNoTracking().ToListAsync();
        Assert.HasCount(1, rows);
        Assert.AreEqual(SetupGuidanceSettings.SingletonId, rows[0].Id);
        Assert.AreEqual(sender, rows[0].KindleSenderAddress);
        Assert.AreEqual(serverName, rows[0].MessagingServerName);
    }

    private static CwaSettings KindleReadyCwa(ICredentialProtector protector)
    {
        var cwa = new CwaSettings(Now);
        cwa.SetSettings(CwaTransportMode.Local, "/data/cwa-ingest", null, null, null, null,
            CwaSftpAuthenticationMode.PrivateKey, "http://cwa.example.test", null, null, "delivery-account", null, Now);
        cwa.SetEreaderServiceAccountPassword(
            protector.Protect(PublishingSecretPurposes.CwaEreaderServiceAccountPassword, "delivery-password"),
            protector.FormatVersion, null, null, Now);
        return cwa;
    }

    private static MatrixSettings EnabledMatrix(ICredentialProtector protector)
    {
        var matrix = new MatrixSettings(Now);
        matrix.SetSettings("http://matrix-internal.example.test", "@librarian:example.test", null, Now);
        matrix.SetAccessToken(protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "stand-in-token"),
            protector.FormatVersion, null, Now);
        matrix.SetEnabled(true, null, Now);
        return matrix;
    }

    private static async Task ChangeAsync(
        FamilyLibrarianAppFactory factory, Func<AppDbContext, ICredentialProtector, Task> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await change(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<ICredentialProtector>());
    }

    private static async Task<HttpClient> SignInAsync(FamilyLibrarianAppFactory factory, string email, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    private static string[] Available(UserSetupResponse setup) =>
        setup.Steps.Where(step => step.IsAvailable).Select(step => step.Step).ToArray();

    private static UserSetupStepResponse Step(UserSetupResponse setup, string step) =>
        setup.Steps.Single(candidate => candidate.Step == step);

    private static async Task<UserSetupResponse> GetMineAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UserSetupResponse>(Mine))!;

    private static async Task<HttpClient> WithTokenAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private sealed class CountingGuard : IAcquisitionBoundaryGuard
    {
        private int calls;

        public int Calls => calls;

        public Task<bool> CanAcceptNewArtifactAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(true);
        }
    }

    private sealed class InterruptedStore(ISetupGuidanceStore inner, Func<Task> beforeFirstSave) : ISetupGuidanceStore
    {
        private bool interrupted;

        public Task<SetupGuidanceSettings?> FindAsync(CancellationToken cancellationToken) =>
            inner.FindAsync(cancellationToken);

        public Task<SetupGuidanceSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            inner.GetOrCreateAsync(cancellationToken);

        public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
        {
            if (!interrupted)
            {
                interrupted = true;
                await beforeFirstSave();
            }

            return await inner.TrySaveChangesAsync(cancellationToken);
        }
    }
}
