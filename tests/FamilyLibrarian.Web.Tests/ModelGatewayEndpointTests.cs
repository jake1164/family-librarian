using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M4-0 over the real host: admin-only, off by default, key write-only, no route that calls a model.</summary>
[TestClass]
public sealed class ModelGatewayEndpointTests
{
    private const string BasePath = "/api/v1/admin/model-gateway";
    private static readonly string[] ExpectedRoles =
    [
        "IntentInterpretation", "ConversationResponse", "CandidateAmbiguityAssessment",
        "ArtifactValidationAssessment", "VisionCoverIdentification", "SearchGuidance"
    ];
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

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password, bool token = true)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (token) http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    [TestMethod]
    public async Task AnonymousIsChallengedAndAMemberIsForbiddenOnEveryRoute()
    {
        await using var factory = Factory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{BasePath}/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync($"{BasePath}/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync($"{BasePath}/models")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{BasePath}/models")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, []))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/enabled", new SetModelGatewayEnabledRequest(true))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"{BasePath}/api-key", new SetModelGatewayApiKeyRequest("sk-member"))).StatusCode);
    }

    [TestMethod]
    public async Task TheModelListWithoutAnEndpointIsAnEmptyListAndAFriendlyProblemNotAnError()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        using var cleared = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest("", false, []));
        Assert.IsTrue(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());

        var list = await admin.GetFromJsonAsync<ModelListResponse>($"{BasePath}/models");

        Assert.IsEmpty(list!.Models);
        StringAssert.Contains(list.Problem, "endpoint");
    }

    [TestMethod]
    public async Task AMutationWithoutAnAntiforgeryTokenIsRejected()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword, token: false);

        var response = await admin.PutAsJsonAsync($"{BasePath}/enabled", new SetModelGatewayEnabledRequest(true));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task EverythingIsOffByDefaultAndTheKeyIsNeverReturned()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        var initial = await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/");
        Assert.IsFalse(initial!.IsEnabled);
        Assert.IsFalse(initial.AllowCloud);
        Assert.IsFalse(initial.HasApiKey);
        Assert.AreEqual(6, initial.Roles.Count);
        CollectionAssert.AreEquivalent(ExpectedRoles, initial.Roles.Select(role => role.Role).ToArray());
        Assert.IsTrue(initial.Roles.All(role => !role.IsEnabled));

        using var stored = await admin.PutAsJsonAsync($"{BasePath}/api-key", new SetModelGatewayApiKeyRequest("sk-super-secret-value"));
        var body = await stored.Content.ReadAsStringAsync();
        Assert.IsTrue(stored.IsSuccessStatusCode, body);
        Assert.IsFalse(body.Contains("sk-super-secret-value", StringComparison.Ordinal));
        var reread = await admin.GetStringAsync($"{BasePath}/");
        Assert.IsFalse(reread.Contains("sk-super-secret-value", StringComparison.Ordinal));
        Assert.IsTrue((await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/"))!.HasApiKey);
    }

    [TestMethod]
    public async Task LimitsDefaultToModestValuesRoundTripAndRejectNonsense()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        var initial = (await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/"))!.Limits;
        Assert.AreEqual(30, initial.TimeoutSeconds);
        Assert.AreEqual(0, initial.CloudDailyRequests, "Cloud allowance is zero until granted.");
        Assert.AreEqual(30, initial.Interactive.UserDailyRequests);

        var tuned = initial with { MaxConcurrentCalls = 3, Background = new ModelBudgetDto(0, 0, 50, 100_000) };
        using var saved = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, [], tuned));
        using var invalid = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, [], initial with { TimeoutSeconds = 1 }));
        using var negative = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, [], initial with { Interactive = new ModelBudgetDto(-5, 0, 0, 0) }));

        Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var reread = (await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/"))!.Limits;
        Assert.AreEqual(3, reread.MaxConcurrentCalls);
        Assert.AreEqual(50, reread.Background.InstallationDailyRequests);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [TestMethod]
    public async Task TheLedgerCountsReservationsAndNeverTurnsMissingUsageIntoZero()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<FamilyLibrarian.Application.ModelGateway.IModelUsageLedger>();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        var withUsage = new FamilyLibrarian.Domain.ModelGateway.ModelUsageRecord(
            FamilyLibrarian.Domain.ModelGateway.ModelRole.CandidateAmbiguityAssessment, FamilyLibrarian.Domain.ModelGateway.ModelBudgetClass.InteractiveChat, user, "m", false, 900, now);
        var withoutUsage = new FamilyLibrarian.Domain.ModelGateway.ModelUsageRecord(
            FamilyLibrarian.Domain.ModelGateway.ModelRole.CandidateAmbiguityAssessment, FamilyLibrarian.Domain.ModelGateway.ModelBudgetClass.InteractiveChat, user, "m", true, 900, now);
        var pending = new FamilyLibrarian.Domain.ModelGateway.ModelUsageRecord(
            FamilyLibrarian.Domain.ModelGateway.ModelRole.ArtifactValidationAssessment, FamilyLibrarian.Domain.ModelGateway.ModelBudgetClass.BackgroundValidation, null, "m", false, 700, now);
        foreach (var record in new[] { withUsage, withoutUsage, pending }) await ledger.AddAsync(record, CancellationToken.None);

        await ledger.CompleteAsync(withUsage.Id, FamilyLibrarian.Domain.ModelGateway.ModelCallOutcome.Succeeded, 40,
            new FamilyLibrarian.Application.ModelTesting.ModelUsage(10, 20, 30), CancellationToken.None);
        await ledger.CompleteAsync(withoutUsage.Id, FamilyLibrarian.Domain.ModelGateway.ModelCallOutcome.EmptyContent, 40, null, CancellationToken.None);

        var totals = await ledger.GetTotalsAsync(FamilyLibrarian.Domain.ModelGateway.ModelBudgetClass.InteractiveChat, user, now.AddMinutes(-1), CancellationToken.None);
        Assert.AreEqual(2, totals.UserRequests);
        Assert.AreEqual(30 + 900, totals.UserTokens, "Reported usage is used when present; an unreported call keeps its conservative estimate.");
        Assert.AreEqual(1, totals.CloudRequests);
        var background = await ledger.GetTotalsAsync(FamilyLibrarian.Domain.ModelGateway.ModelBudgetClass.BackgroundValidation, null, now.AddMinutes(-1), CancellationToken.None);
        Assert.AreEqual(1, background.InstallationRequests, "A pending reservation counts against the budget.");

        var summary = await ledger.SummarizeAsync(now.AddMinutes(-1), CancellationToken.None);
        var empty = summary.Rows.Single(row => row.Outcome == FamilyLibrarian.Domain.ModelGateway.ModelCallOutcome.EmptyContent);
        Assert.AreEqual(0, empty.ReportedTokens);
        Assert.AreEqual(1, empty.CallsWithoutUsage, "Missing usage is counted as missing, not as zero tokens.");
    }

    [TestMethod]
    public async Task UsageIsAdministratorOnlyAndContainsNoPromptsOrReplies()
    {
        await using var factory = Factory();
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync($"{BasePath}/usage")).StatusCode);
        var usage = await admin.GetFromJsonAsync<ModelUsageResponse>($"{BasePath}/usage");
        Assert.IsNotNull(usage);
    }

    [TestMethod]
    public async Task SendingFileDetailsToACloudModelIsASeparateOptInThatNeedsCloudUse()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

        Assert.IsFalse((await admin.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/"))!.AllowCloudExcerpts);
        using var refused = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, [], null, true));
        using var cloudOnly = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, true, [], null, null));
        var afterCloudOnly = (await cloudOnly.Content.ReadFromJsonAsync<ModelGatewaySettingsResponse>())!;
        using var both = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, true, [], null, true));
        var afterBoth = (await both.Content.ReadFromJsonAsync<ModelGatewaySettingsResponse>())!;
        using var cloudOff = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, []));
        var afterOff = (await cloudOff.Content.ReadFromJsonAsync<ModelGatewaySettingsResponse>())!;

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.IsTrue(afterCloudOnly.AllowCloud);
        Assert.IsFalse(afterCloudOnly.AllowCloudExcerpts, "Allowing cloud use does not allow sharing file details.");
        Assert.IsTrue(afterBoth.AllowCloudExcerpts);
        Assert.IsFalse(afterOff.AllowCloudExcerpts, "Switching cloud use off switches detail sharing off too.");
    }

    [TestMethod]
    public async Task ConfiguringRolesRoundTripsAndCloudNeedsExplicitOptIn()
    {
        await using var factory = Factory();
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var cloudRole = new ModelRoleDto("ArtifactValidationAssessment", true, "Cloud", null, "cloud/alias");

        using var refused = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest("http://litellm.example.test:4000", false, [cloudRole]));
        using var unknown = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(null, false, [new ModelRoleDto("Nonsense", true, "Local", "a", null)]));
        using var saved = await admin.PutAsJsonAsync($"{BasePath}/", new SaveModelGatewaySettingsRequest(
            "http://litellm.example.test:4000", true, [cloudRole, new ModelRoleDto("CandidateAmbiguityAssessment", true, "Local", "local/alias", null)]));

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var settings = (await saved.Content.ReadFromJsonAsync<ModelGatewaySettingsResponse>())!;
        Assert.IsTrue(settings.AllowCloud);
        Assert.IsFalse(settings.IsEnabled, "Saving never turns the master switch on.");
        Assert.AreEqual("local/alias", settings.Roles.Single(role => role.Role == "CandidateAmbiguityAssessment").LocalModel);
        Assert.AreEqual("Cloud", settings.Roles.Single(role => role.Role == "ArtifactValidationAssessment").Placement);
    }
}
