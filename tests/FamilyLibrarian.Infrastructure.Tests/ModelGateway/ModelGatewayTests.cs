using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelGateway;

internal sealed class FakeGatewayStore : IModelGatewaySettingsStore
{
    public ModelGatewaySettings? Settings { get; set; }

    public Task<ModelGatewaySettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task<ModelGatewaySettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Settings ??= new ModelGatewaySettings(new FakeClock().UtcNow));

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>M4-0: production model configuration is off by default, admin-only, and never falls back to a cloud model.</summary>
[TestClass]
public sealed class ModelGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static (ModelGatewaySettingsService Service, FakeGatewayStore Store, CapturingAudit Audit) Create(bool admin = true)
    {
        var store = new FakeGatewayStore();
        var audit = new CapturingAudit();
        var service = new ModelGatewaySettingsService(store, new PassThroughProtector(), audit, new FakeUser(admin ? Guid.NewGuid() : null), new FakeAdmin(admin), new FakeClock());
        return (service, store, audit);
    }

    private static ModelRoleCommand Role(ModelRole role, bool enabled = true, ModelPlacement placement = ModelPlacement.Local, string? local = "local/alias", string? cloud = null) =>
        new(role, enabled, placement, local, cloud);

    private static ModelGatewaySettings Configured(
        bool enabled = true, bool allowCloud = false, bool withKey = true, ModelRole role = ModelRole.CandidateAmbiguityAssessment,
        bool roleEnabled = true, ModelPlacement placement = ModelPlacement.Local, string? local = "local/alias", string? cloud = "cloud/alias")
    {
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000/", allowCloud, null, Now);
        settings.ConfigureRole(role, roleEnabled, placement, local, cloud, null, Now);
        if (withKey) settings.SetApiKey("p:the-secret", 1, null, Now);
        if (enabled) settings.SetEnabled(true, null, Now);
        return settings;
    }

    private static ModelRoleResolver Resolver(ModelGatewaySettings? settings) =>
        new(new FakeGatewayStore { Settings = settings }, new PassThroughProtector());

    [TestMethod]
    public async Task EveryRoleIsOffAndNothingIsConfiguredByDefault()
    {
        var (service, _, _) = Create();

        var view = (await service.GetAsync(CancellationToken.None)).Settings!;

        Assert.IsFalse(view.IsEnabled);
        Assert.IsFalse(view.AllowCloud);
        Assert.IsNull(view.EndpointUrl);
        Assert.AreEqual(Enum.GetValues<ModelRole>().Length, view.Roles.Count);
        Assert.IsTrue(view.Roles.All(role => !role.IsEnabled && role.LocalModel is null && role.CloudModel is null));
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var resolution = await Resolver(null).ResolveAsync(role, CancellationToken.None);
            Assert.IsFalse(resolution.IsAvailable);
            Assert.AreEqual(ModelUnavailableReason.GatewayDisabled, resolution.Reason);
        }
    }

    [TestMethod]
    public async Task OnlyAnAuthenticatedAdministratorMayReadOrChangeAnything()
    {
        var (service, store, _) = Create(admin: false);

        Assert.IsTrue((await service.GetAsync(CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.SaveAsync(new SaveModelGatewaySettings("http://x.example.test", false, [Role(ModelRole.IntentInterpretation)]), CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.SetEnabledAsync(true, CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.SetApiKeyAsync("sk-1", CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.ClearApiKeyAsync(CancellationToken.None)).Forbidden);
        Assert.IsNull(store.Settings, "A refused call must not even create the settings row.");
    }

    [TestMethod]
    public async Task SavingStoresRolesAuditsWithoutSecretsAndNeverEnablesTheGateway()
    {
        var (service, store, audit) = Create();
        await service.SetApiKeyAsync("sk-very-secret", CancellationToken.None);

        var result = await service.SaveAsync(
            new SaveModelGatewaySettings("http://litellm.example.test:4000/", false,
                [Role(ModelRole.CandidateAmbiguityAssessment), Role(ModelRole.IntentInterpretation, enabled: false, local: null)]),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsFalse(result.Settings!.IsEnabled, "Saving roles must not switch the master switch on.");
        Assert.AreEqual("http://litellm.example.test:4000", store.Settings!.EndpointUrl);
        Assert.IsTrue(result.Settings.Roles.Single(role => role.Role == ModelRole.CandidateAmbiguityAssessment).IsEnabled);
        Assert.IsTrue(audit.Events.All(entry => !entry.Json.Contains("sk-very-secret", StringComparison.Ordinal)));
        Assert.IsTrue(result.Settings.HasApiKey);
    }

    [TestMethod]
    public async Task EnablingNeedsAnEndpointAndAnEnabledRoleNeedsItsOwnAlias()
    {
        var (service, _, _) = Create();

        Assert.IsFalse((await service.SetEnabledAsync(true, CancellationToken.None)).Succeeded, "No endpoint yet.");
        var noAlias = await service.SaveAsync(new SaveModelGatewaySettings("http://x.example.test", false, [Role(ModelRole.IntentInterpretation, local: null)]), CancellationToken.None);
        Assert.IsFalse(noAlias.Succeeded);
        StringAssert.Contains(noAlias.Error!, "alias");
    }

    [TestMethod]
    public async Task CloudPlacementIsRefusedUnlessCloudIsExplicitlyAllowed()
    {
        var (service, _, _) = Create();
        var cloudRole = Role(ModelRole.ArtifactValidationAssessment, placement: ModelPlacement.Cloud, local: null, cloud: "cloud/alias");

        var refused = await service.SaveAsync(new SaveModelGatewaySettings("http://x.example.test", false, [cloudRole]), CancellationToken.None);
        var allowed = await service.SaveAsync(new SaveModelGatewaySettings("http://x.example.test", true, [cloudRole]), CancellationToken.None);

        Assert.IsFalse(refused.Succeeded);
        Assert.IsTrue(allowed.Succeeded, allowed.Error);
    }

    [TestMethod]
    public async Task InvalidEndpointsAliasesAndDuplicateRolesAreRejected()
    {
        var (service, _, _) = Create();
        foreach (var endpoint in new[] { "ftp://x.example.test", "http://user:pw@x.example.test", "http://x.example.test/?key=1", "not a url" })
        {
            Assert.IsFalse((await service.SaveAsync(new SaveModelGatewaySettings(endpoint, false, []), CancellationToken.None)).Succeeded, endpoint);
        }

        Assert.IsFalse((await service.SaveAsync(new SaveModelGatewaySettings(null, false, [Role(ModelRole.IntentInterpretation, local: "bad alias!")]), CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SaveAsync(
            new SaveModelGatewaySettings(null, false, [Role(ModelRole.IntentInterpretation), Role(ModelRole.IntentInterpretation)]), CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SetApiKeyAsync("bad\nkey", CancellationToken.None)).Succeeded);
    }

    [TestMethod]
    public async Task AResolvedRoleUsesOnlyItsOwnPlacementAndTheStoredKey()
    {
        var resolution = await Resolver(Configured()).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None);

        Assert.IsTrue(resolution.IsAvailable);
        Assert.AreEqual("local/alias", resolution.Model);
        Assert.IsFalse(resolution.IsCloud);
        Assert.AreEqual("the-secret", resolution.ApiKey);
        Assert.AreEqual("http://litellm.example.test:4000/", resolution.Endpoint!.ToString());
    }

    [TestMethod]
    public async Task AnUnconfiguredOrDisabledRoleIsUnavailableEvenWhenOthersAreOn()
    {
        var resolver = Resolver(Configured());

        Assert.AreEqual(ModelUnavailableReason.RoleDisabled, (await resolver.ResolveAsync(ModelRole.IntentInterpretation, CancellationToken.None)).Reason);
        Assert.AreEqual(ModelUnavailableReason.RoleDisabled,
            (await Resolver(Configured(roleEnabled: false)).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None)).Reason);
        Assert.AreEqual(ModelUnavailableReason.GatewayDisabled,
            (await Resolver(Configured(enabled: false)).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None)).Reason);
    }

    [TestMethod]
    public async Task ACloudRoleNeverFallsBackToItsLocalAliasAndALocalRoleNeverUsesTheCloudAlias()
    {
        var cloudNotAllowed = Configured(allowCloud: false, placement: ModelPlacement.Cloud);
        var cloudAllowed = Configured(allowCloud: true, placement: ModelPlacement.Cloud);
        var localWithCloudAliasToo = Configured(allowCloud: true, placement: ModelPlacement.Local);

        var refused = await Resolver(cloudNotAllowed).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None);
        var cloud = await Resolver(cloudAllowed).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None);
        var local = await Resolver(localWithCloudAliasToo).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None);

        Assert.AreEqual(ModelUnavailableReason.CloudNotAllowed, refused.Reason);
        Assert.IsNull(refused.Model, "A refused cloud role must not hand back the local alias as a fallback.");
        Assert.IsTrue(cloud.IsCloud);
        Assert.AreEqual("cloud/alias", cloud.Model);
        Assert.IsFalse(local.IsCloud);
        Assert.AreEqual("local/alias", local.Model);
    }

    [TestMethod]
    public async Task AMissingAliasOrUnreadableKeyMakesTheRoleUnavailableRatherThanGuessing()
    {
        Assert.AreEqual(ModelUnavailableReason.NotConfigured,
            (await Resolver(Configured(local: null)).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None)).Reason);
        var unreadable = Configured(withKey: false);
        unreadable.SetApiKey("not-a-protected-value", 1, null, Now);
        Assert.AreEqual(ModelUnavailableReason.ApiKeyUnreadable,
            (await Resolver(unreadable).ResolveAsync(ModelRole.CandidateAmbiguityAssessment, CancellationToken.None)).Reason);
    }
}
