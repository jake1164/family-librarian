using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Contracts.Setup;
using FamilyLibrarian.Contracts.Delivery;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>First-time setup: what a member is offered, what is remembered, and who may edit the guidance.</summary>
[TestClass]
public sealed class SetupEndpointTests
{
    private const string Mine = "/api/v1/me/setup/";
    private const string Guidance = "/api/v1/admin/setup-guidance/";
    private const string BotToken = "setup-test-bot-token-do-not-leak-8675309";

    // The test host reports both formats as ready, so only the audiobook-driven format step is available at first.
    [TestMethod]
    public async Task ANewAccountIsOfferedTheWizardOnceAndLaterFeaturesBecomeAReminder()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var user = await WithTokenAsync(await fixture.CreateUserClientAsync());
        using var admin = await WithTokenAsync(await fixture.CreateAdminClientAsync());

        var first = await GetMineAsync(user);
        Assert.IsFalse(first.WizardSeen);
        Assert.IsTrue(first.ShouldOpenWizard);
        Assert.IsTrue(Step(first, SetupStepNames.Formats).IsAvailable);
        Assert.IsFalse(Step(first, SetupStepNames.Kindle).IsAvailable);
        Assert.IsFalse(Step(first, SetupStepNames.Messaging).IsAvailable);
        Assert.IsEmpty(first.ReminderSteps);
        Assert.IsNull(first.Kindle);
        Assert.IsNull(first.Messaging);

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PostAsync($"{Mine}seen", null)).StatusCode);
        var seen = await GetMineAsync(user);
        Assert.IsTrue(seen.WizardSeen);
        Assert.IsFalse(seen.ShouldOpenWizard);

        await ConfigureKindleDeliveryAsync(admin);

        var later = await GetMineAsync(user);
        Assert.IsFalse(later.ShouldOpenWizard);
        CollectionAssert.AreEqual(new[] { SetupStepNames.Kindle }, later.ReminderSteps.ToArray());
        Assert.IsNotNull(later.Kindle);

        // Dismissing is per account.
        var skipped = await user.PostAsJsonAsync($"{Mine}skipped", new SkipSetupStepsRequest([SetupStepNames.Kindle]));
        Assert.AreEqual(HttpStatusCode.NoContent, skipped.StatusCode);
        Assert.IsEmpty((await GetMineAsync(user)).ReminderSteps);
        Assert.IsTrue(Step(await GetMineAsync(user), SetupStepNames.Kindle).IsSkipped);
        CollectionAssert.Contains((await GetMineAsync(admin)).ReminderSteps.ToArray(), SetupStepNames.Kindle);
    }

    [TestMethod]
    public async Task SavedSettingsCountAsSetUpAndAFinishedStepStaysFinished()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var user = await WithTokenAsync(await fixture.CreateUserClientAsync());
        using var admin = await WithTokenAsync(await fixture.CreateAdminClientAsync());
        await ConfigureKindleDeliveryAsync(admin);

        var added = await user.PutAsJsonAsync(
            "/api/v1/me/delivery/kindle", new SetKindleAddressRequest("reader@kindle.com", null, true));
        Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);
        Assert.IsTrue(Step(await GetMineAsync(user), SetupStepNames.Kindle).IsSet);
        Assert.IsEmpty((await GetMineAsync(user)).ReminderSteps);

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await user.PostAsync($"{Mine}steps/formats/completed", null)).StatusCode);
        await user.PostAsJsonAsync($"{Mine}skipped", new SkipSetupStepsRequest([SetupStepNames.Formats]));
        var formats = Step(await GetMineAsync(user), SetupStepNames.Formats);
        Assert.IsTrue(formats.IsCompleted);
        Assert.IsTrue(formats.IsSet);
        Assert.IsFalse(formats.IsSkipped);
        Assert.IsFalse((await GetMineAsync(user)).ShouldOpenWizard);
    }

    [TestMethod]
    [DataRow("steps/7/completed")]
    [DataRow("steps/Formats,Kindle/completed")]
    [DataRow("steps/QuietHours/completed")]
    public async Task OnlyASingleNamedStepCanBeRecorded(string path)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var user = await WithTokenAsync(await fixture.CreateUserClientAsync());

        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PostAsync($"{Mine}{path}", null)).StatusCode);
        var skipped = await user.PostAsJsonAsync($"{Mine}skipped", new SkipSetupStepsRequest(["3"]));
        Assert.AreEqual(HttpStatusCode.BadRequest, skipped.StatusCode);
        Assert.IsTrue((await GetMineAsync(user)).Steps.All(step => !step.IsCompleted && !step.IsSkipped));
    }

    [TestMethod]
    public async Task MembersSeeTheGuidanceButNothingSecretAndCannotEditIt()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var user = await WithTokenAsync(await fixture.CreateUserClientAsync());
        using var admin = await WithTokenAsync(await fixture.CreateAdminClientAsync());
        await ConfigureKindleDeliveryAsync(admin);
        await ConfigureMatrixAsync(fixture);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync(Guidance)).StatusCode);
        var refused = await user.PutAsJsonAsync($"{Guidance}kindle", new SetKindleGuidanceRequest("a@example.org", null));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);

        // Before anything is saved: the default store, and the server taken from the bot's ID.
        var defaults = await GetMineAsync(user);
        Assert.IsNull(defaults.Kindle!.SenderAddress);
        Assert.AreEqual("https://www.amazon.com/sendtokindle/email", defaults.Kindle.FindAddressUrl);
        Assert.AreEqual("example.test", defaults.Messaging!.ServerName);
        Assert.IsTrue(defaults.Messaging.ShowElementHelp);

        var kindle = await admin.PutAsJsonAsync($"{Guidance}kindle", new SetKindleGuidanceRequest(
            "library@example.org", "https://www.amazon.co.uk/gp/help"));
        Assert.AreEqual(HttpStatusCode.OK, kindle.StatusCode);
        var messaging = await admin.PutAsJsonAsync($"{Guidance}messaging", new SetMessagingGuidanceRequest(
            "chat.example.org", "Use your library login.", "https://help.example.org/", "https://web.example.org/"));
        Assert.AreEqual(HttpStatusCode.OK, messaging.StatusCode);
        var elementHelp = await admin.PutAsJsonAsync(
            $"{Guidance}messaging/element-help", new SetShowElementHelpRequest(false));
        Assert.AreEqual(HttpStatusCode.OK, elementHelp.StatusCode);

        var raw = await user.GetStringAsync(Mine);
        Assert.IsFalse(raw.Contains(BotToken, StringComparison.Ordinal));
        Assert.IsFalse(raw.Contains("matrix-internal.example.test", StringComparison.Ordinal),
            "The homeserver address only has to work from the server and is not shown to members.");

        var saved = await GetMineAsync(user);
        Assert.AreEqual("library@example.org", saved.Kindle!.SenderAddress);
        Assert.AreEqual("https://www.amazon.co.uk/hz/mycd/myx", saved.Kindle.ManageContentUrl);
        Assert.AreEqual("chat.example.org", saved.Messaging!.ServerName);
        Assert.AreEqual("@librarian:example.test", saved.Messaging.BotUserId);
        Assert.AreEqual("Use your library login.", saved.Messaging.SignInInstructions);
        Assert.IsFalse(saved.Messaging.ShowElementHelp);

        var adminView = await admin.GetFromJsonAsync<SetupGuidanceSettingsResponse>(Guidance);
        Assert.AreEqual("example.test", adminView!.SuggestedServerName);
        Assert.AreEqual("chat.example.org", adminView.MessagingServerName);
        Assert.AreEqual("amazon.co.uk", adminView.AmazonStoreDomain);

        var invalid = await admin.PutAsJsonAsync($"{Guidance}kindle", new SetKindleGuidanceRequest(null, "example.net"));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual("library@example.org", (await GetMineAsync(user)).Kindle!.SenderAddress);
    }

    [TestMethod]
    public async Task GuidanceEditsNeedAnAntiforgeryToken()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.PutAsJsonAsync($"{Guidance}kindle", new SetKindleGuidanceRequest(null, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Mine}seen", null)).StatusCode);
    }

    [TestMethod]
    public async Task UpgradingMarksExistingAccountsAsAlreadyOfferedTheWizard()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrations = database.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddSetupWizard", StringComparison.Ordinal));
        Assert.IsTrue(index > 0);
        var accounts = await database.Users.CountAsync();
        Assert.IsTrue(accounts > 0);

        await database.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        await database.Database.MigrateAsync();
        database.ChangeTracker.Clear();

        var users = await database.Users.AsNoTracking().ToListAsync();
        Assert.HasCount(accounts, users);
        Assert.IsTrue(users.All(user => user.SetupWizardSeenAtUtc is not null));
        Assert.IsFalse(database.Database.HasPendingModelChanges());
    }

    private static UserSetupStepResponse Step(UserSetupResponse setup, string step) =>
        setup.Steps.Single(candidate => candidate.Step == step);

    private static async Task<UserSetupResponse> GetMineAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UserSetupResponse>(Mine))!;

    private static async Task ConfigureKindleDeliveryAsync(HttpClient admin)
    {
        var settings = await admin.PutAsJsonAsync("/api/v1/admin/publishing/cwa/", new SetCwaSettingsRequest(
            "Local", "/data/cwa-ingest", null, null, null, null, "PrivateKey", "http://cwa.example.test", null, null,
            "delivery-account"));
        Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
        var password = await admin.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/ereader-service-account-password",
            new SetPublishingSecretRequest("delivery-password"));
        Assert.AreEqual(HttpStatusCode.OK, password.StatusCode);
    }

    // Saved directly: enabling through the endpoint would need a live homeserver to pass the connection test.
    private static async Task ConfigureMatrixAsync(WebTestFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
        var matrix = new MatrixSettings(DateTimeOffset.UtcNow);
        matrix.SetSettings(
            "http://matrix-internal.example.test", "@librarian:example.test", null, DateTimeOffset.UtcNow);
        matrix.SetAccessToken(
            protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, BotToken),
            protector.FormatVersion, null, DateTimeOffset.UtcNow);
        matrix.SetEnabled(true, null, DateTimeOffset.UtcNow);
        database.MatrixSettings.Add(matrix);
        await database.SaveChangesAsync();
    }

    private static async Task<HttpClient> WithTokenAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }
}
