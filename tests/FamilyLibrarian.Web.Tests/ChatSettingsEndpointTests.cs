using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Contracts.Conversation;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>The chat switches over the real host and PostgreSQL: who may use them, and that they take effect without a restart.</summary>
[TestClass]
public sealed class ChatSettingsEndpointTests
{
    private const string Path = "/api/v1/admin/chat/settings";
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    [TestCleanup]
    public async Task ForgetTheSavedChoiceAsync()
    {
        if (fixture is null) return;
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatSettings.ExecuteDeleteAsync();
    }

    private static FamilyLibrarianAppFactory Factory(ConversationOptions? configured = null) =>
        configured is null
            ? new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString)
            : new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
            {
                services.RemoveAll<ConversationOptions>();
                services.AddSingleton(configured);
            });

    private static async Task<HttpClient> SignedInAsync(
        FamilyLibrarianAppFactory factory, string email, string password, bool withToken = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (withToken)
        {
            client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        }

        return client;
    }

    private static Task<HttpClient> AdminAsync(FamilyLibrarianAppFactory factory, bool withToken = true) =>
        SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword, withToken);

    private static Task<HttpClient> ReaderAsync(FamilyLibrarianAppFactory factory) =>
        SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

    private static async Task<bool> WebChatOnAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ConversationStatusResponse>("/api/v1/conversation/status"))!.Enabled;

    private static Task<HttpResponseMessage> SayHelpAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/conversation/messages", new ConversationMessageRequest("help", Guid.NewGuid().ToString("N")));

    [TestMethod]
    public async Task AnAdministratorTurnsWebChatOnAndOffWithoutARestart()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        using var reader = await ReaderAsync(factory);
        Assert.IsFalse(await WebChatOnAsync(reader), "Chat starts off on a default installation.");

        using var on = await admin.PutAsJsonAsync(Path, new SetChatSettingsRequest(WebEnabled: true));
        var saved = await on.Content.ReadFromJsonAsync<ChatSettingsResponse>();

        Assert.AreEqual(HttpStatusCode.OK, on.StatusCode);
        Assert.AreEqual(new ChatSettingsResponse(true, false, false), saved);
        Assert.IsTrue(await WebChatOnAsync(reader));
        using var answered = await SayHelpAsync(reader);
        Assert.AreEqual(HttpStatusCode.OK, answered.StatusCode);

        using var off = await admin.PutAsJsonAsync(Path, new SetChatSettingsRequest(WebEnabled: false));

        Assert.AreEqual(HttpStatusCode.OK, off.StatusCode);
        Assert.IsFalse(await WebChatOnAsync(reader));
        using var refused = await SayHelpAsync(reader);
        Assert.AreEqual(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [TestMethod]
    public async Task ASavedChoiceOverridesConfigurationAndSurvivesARestart()
    {
        var configured = new ConversationOptions { Enabled = true, WebEnabled = true, MatrixEnabled = true };
        await using (var first = Factory(configured))
        {
            using var admin = await AdminAsync(first);
            Assert.AreEqual(new ChatSettingsResponse(true, true, false), await admin.GetFromJsonAsync<ChatSettingsResponse>(Path));
            using var saved = await admin.PutAsJsonAsync(Path, new SetChatSettingsRequest(WebEnabled: false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        }

        await using var restarted = Factory(configured);
        using var reader = await ReaderAsync(restarted);

        Assert.IsFalse(await WebChatOnAsync(reader));
    }

    [TestMethod]
    public async Task OnlyAnAdministratorWithAnAntiforgeryTokenCanUseTheSwitches()
    {
        await using var factory = Factory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var member = await ReaderAsync(factory);
        using var forgeable = await AdminAsync(factory, withToken: false);
        var change = new SetChatSettingsRequest(WebEnabled: true);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync(Path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(Path, change)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await forgeable.PutAsJsonAsync(Path, change)).StatusCode);
        Assert.IsFalse(await WebChatOnAsync(member), "Nothing refused above changed anything.");
    }

    [TestMethod]
    public async Task ARequestThatNamesNoSwitchIsRejected()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);

        using var response = await admin.PutAsJsonAsync(Path, new SetChatSettingsRequest());

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
