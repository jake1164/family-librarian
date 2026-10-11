using System.Reflection;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelGateway;

/// <summary>
/// The AI administration consolidation: the older separate model-test connection is merged into the one shared connection
/// only when that is unambiguous, a disagreement is preserved until an administrator resolves it, and the test harness can
/// only ever read the gateway.
/// </summary>
[TestClass]
public sealed class ModelConnectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ReadOnlyMethods = ["FindRoleTargetAsync", "GetConnectionAsync"];

    private sealed class Rig
    {
        public Rig(bool admin = true)
        {
            Service = new ModelConnectionService(
                Gateway, Legacy, Protector, Audit, new FakeUser(admin ? Guid.NewGuid() : null), new FakeAdmin(admin), new FakeClock());
        }

        public FakeGatewayStore Gateway { get; } = new();

        public FakeSettingsStore Legacy { get; } = new();

        public PassThroughProtector Protector { get; } = new();

        public CapturingAudit Audit { get; } = new();

        public ModelConnectionService Service { get; }

        public Rig WithLegacy(string? endpoint, string? key)
        {
            var settings = new ModelTestSettings(Now);
            settings.SetLegacyEndpoint(endpoint, null, Now);
            if (key is not null) settings.SetApiKey($"p:{key}", 1, null, Now);
            Legacy.Settings = settings;
            return this;
        }

        public Rig WithShared(string? endpoint, string? key, bool enabled = false, bool allowCloud = false)
        {
            var settings = new ModelGatewaySettings(Now);
            settings.Configure(endpoint, allowCloud, null, Now);
            if (key is not null) settings.SetApiKey($"p:{key}", 1, null, Now);
            if (enabled) settings.SetEnabled(true, null, Now);
            Gateway.Settings = settings;
            return this;
        }
    }

    [TestMethod]
    public async Task WithNoOlderConnectionNothingIsReadAsAConflictOrWritten()
    {
        var rig = new Rig().WithShared("http://shared.example.test", "k1");

        var result = await rig.Service.ReconcileAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Notice!.HasConflict);
        Assert.IsEmpty(rig.Audit.Events);
        Assert.AreEqual(0, rig.Legacy.Saves);
    }

    [TestMethod]
    public async Task AnOnlyTestConnectionMovesToTheSharedOneWithoutSwitchingAnythingOn()
    {
        var rig = new Rig().WithLegacy("http://old.example.test", "legacy-key");

        await rig.Service.ReconcileAsync(CancellationToken.None);

        var shared = rig.Gateway.Settings!;
        Assert.AreEqual("http://old.example.test", shared.EndpointUrl);
        Assert.IsTrue(shared.HasApiKey);
        Assert.AreEqual("legacy-key", rig.Protector.Unprotect(ModelGatewaySecretPurposes.ApiKey, shared.ProtectedApiKey!, shared.ApiKeyFormatVersion));
        Assert.IsFalse(shared.IsEnabled);
        Assert.IsFalse(shared.AllowCloud);
        Assert.IsFalse(shared.AllowCloudExcerpts);
        Assert.IsEmpty(shared.Roles, "No role is created or enabled by a connection merge.");
        Assert.IsNull(rig.Legacy.Settings!.EndpointUrl);
        Assert.IsFalse(rig.Legacy.Settings.HasApiKey);
        Assert.IsFalse(rig.Audit.Events.Single().Json.Contains("legacy-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheSameConnectionInBothJustDropsTheDuplicate()
    {
        var rig = new Rig().WithShared("http://same.example.test", "same-key", enabled: true).WithLegacy("http://same.example.test", "same-key");

        await rig.Service.ReconcileAsync(CancellationToken.None);

        Assert.AreEqual("http://same.example.test", rig.Gateway.Settings!.EndpointUrl);
        Assert.IsTrue(rig.Gateway.Settings.IsEnabled, "Production state is untouched.");
        Assert.IsNull(rig.Legacy.Settings!.EndpointUrl);
        Assert.IsFalse(rig.Legacy.Settings.HasApiKey);
    }

    [TestMethod]
    public async Task ASharedConnectionWithoutAKeyReceivesTheTestKeyAtTheSameEndpoint()
    {
        var rig = new Rig().WithShared("http://same.example.test", null).WithLegacy("http://same.example.test", "test-key");

        await rig.Service.ReconcileAsync(CancellationToken.None);

        Assert.AreEqual("test-key", rig.Protector.Unprotect(ModelGatewaySecretPurposes.ApiKey, rig.Gateway.Settings!.ProtectedApiKey!, 1));
    }

    [TestMethod]
    public async Task DifferentEndpointsAreNeverSilentlyChosenBetween()
    {
        var rig = new Rig().WithShared("http://shared.example.test", "k-shared", enabled: true).WithLegacy("http://legacy.example.test", "k-legacy");

        var result = await rig.Service.ReconcileAsync(CancellationToken.None);

        Assert.IsTrue(result.Notice!.HasConflict);
        Assert.AreEqual("http://shared.example.test", rig.Gateway.Settings!.EndpointUrl);
        Assert.AreEqual("http://legacy.example.test", rig.Legacy.Settings!.EndpointUrl, "Both are preserved.");
        Assert.IsTrue(rig.Legacy.Settings.HasApiKey);
        Assert.IsEmpty(rig.Audit.Events);
        Assert.AreEqual(0, rig.Legacy.Saves);
    }

    [TestMethod]
    public async Task TheSameEndpointWithDifferentKeysIsAConflictToo()
    {
        var rig = new Rig().WithShared("http://same.example.test", "key-a").WithLegacy("http://same.example.test", "key-b");

        var result = await rig.Service.ReconcileAsync(CancellationToken.None);

        Assert.IsTrue(result.Notice!.HasConflict);
        Assert.AreEqual("key-b", rig.Protector.Unprotect(ModelTestSecretPurposes.ApiKey, rig.Legacy.Settings!.ProtectedApiKey!, 1));
    }

    [TestMethod]
    public async Task AKeyThatCannotBeReadIsPreservedAsAConflictNotDiscarded()
    {
        var rig = new Rig().WithLegacy("http://old.example.test", "x");
        rig.Legacy.Settings!.SetApiKey("unreadable-garbage", 1, null, Now);

        var result = await rig.Service.ReconcileAsync(CancellationToken.None);

        Assert.IsTrue(result.Notice!.HasConflict);
        Assert.IsNull(rig.Gateway.Settings, "Nothing was created to hold a key that could not be read.");
        Assert.AreEqual("unreadable-garbage", rig.Legacy.Settings.ProtectedApiKey);
    }

    [TestMethod]
    public async Task TheNoticeNeverCarriesAKey()
    {
        var rig = new Rig().WithShared("http://a.example.test", "k1").WithLegacy("http://b.example.test", "k2");

        var notice = (await rig.Service.ReconcileAsync(CancellationToken.None)).Notice!;

        Assert.IsFalse(typeof(ModelConnectionNotice).GetProperties().Any(property => property.PropertyType == typeof(string) && property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(notice).Contains("k1", StringComparison.Ordinal) || System.Text.Json.JsonSerializer.Serialize(notice).Contains("k2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task KeepingTheSharedConnectionDiscardsOnlyTheTestCopy()
    {
        var rig = new Rig().WithShared("http://shared.example.test", "k-shared", enabled: true).WithLegacy("http://legacy.example.test", "k-legacy");

        var result = await rig.Service.ResolveAsync(ModelConnectionChoice.Shared, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Notice!.HasConflict);
        Assert.AreEqual("http://shared.example.test", rig.Gateway.Settings!.EndpointUrl);
        Assert.IsTrue(rig.Gateway.Settings.IsEnabled);
        Assert.IsNull(rig.Legacy.Settings!.EndpointUrl);
    }

    [TestMethod]
    public async Task AdoptingTheTestConnectionReplacesTheSharedOneAndSwitchesProductionAiOff()
    {
        var rig = new Rig().WithShared("http://shared.example.test", "k-shared", enabled: true, allowCloud: true).WithLegacy("http://legacy.example.test", "k-legacy");

        var result = await rig.Service.ResolveAsync(ModelConnectionChoice.LegacyTest, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("http://legacy.example.test", rig.Gateway.Settings!.EndpointUrl);
        Assert.AreEqual("k-legacy", rig.Protector.Unprotect(ModelGatewaySecretPurposes.ApiKey, rig.Gateway.Settings.ProtectedApiKey!, 1));
        Assert.IsFalse(rig.Gateway.Settings.IsEnabled, "A production connection change leaves AI off until an administrator turns it back on.");
        Assert.IsTrue(rig.Gateway.Settings.AllowCloud, "Cloud permission is not silently changed in either direction.");
        Assert.IsNull(rig.Legacy.Settings!.EndpointUrl);
    }

    [TestMethod]
    public async Task AdoptingAnUnreadableTestKeyIsRefusedAndChangesNothing()
    {
        var rig = new Rig().WithShared("http://shared.example.test", "k-shared").WithLegacy("http://legacy.example.test", "x");
        rig.Legacy.Settings!.SetApiKey("unreadable-garbage", 1, null, Now);

        var result = await rig.Service.ResolveAsync(ModelConnectionChoice.LegacyTest, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("http://shared.example.test", rig.Gateway.Settings!.EndpointUrl);
        Assert.AreEqual("http://legacy.example.test", rig.Legacy.Settings.EndpointUrl);
    }

    [TestMethod]
    public async Task ANonAdministratorCanNeitherReconcileNorResolve()
    {
        var rig = new Rig(admin: false).WithShared("http://a.example.test", "k1").WithLegacy("http://b.example.test", "k2");

        var reconcile = await rig.Service.ReconcileAsync(CancellationToken.None);
        var resolve = await rig.Service.ResolveAsync(ModelConnectionChoice.LegacyTest, CancellationToken.None);

        Assert.IsTrue(reconcile.Forbidden);
        Assert.IsTrue(resolve.Forbidden);
        Assert.AreEqual("http://a.example.test", rig.Gateway.Settings!.EndpointUrl);
        Assert.AreEqual("http://b.example.test", rig.Legacy.Settings!.EndpointUrl);
        Assert.IsEmpty(rig.Audit.Events);
    }

    [TestMethod]
    public async Task TestsUseTheSharedConnectionNormallyAndTheOlderOneOnlyWhileAConflictIsOpen()
    {
        var calm = new Rig().WithShared("http://shared.example.test", "k-shared");
        var conflicted = new Rig().WithShared("http://shared.example.test", "k-shared").WithLegacy("http://legacy.example.test", "k-legacy");

        var shared = await calm.Service.GetConnectionAsync(CancellationToken.None);
        var legacy = await conflicted.Service.GetConnectionAsync(CancellationToken.None);

        Assert.AreEqual("http://shared.example.test/", shared.Endpoint!.ToString());
        Assert.AreEqual("k-shared", shared.ApiKey);
        Assert.IsFalse(shared.IsLegacyConnection);
        Assert.AreEqual("http://legacy.example.test/", legacy.Endpoint!.ToString());
        Assert.AreEqual("k-legacy", legacy.ApiKey);
        Assert.IsTrue(legacy.IsLegacyConnection);
    }

    [TestMethod]
    public async Task ARoleTargetIsReadByItsChosenPlacementOnlyAndNeverWritesAnything()
    {
        var rig = new Rig();
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://shared.example.test", true, null, Now);
        settings.ConfigureRole(ModelRole.IntentInterpretation, true, ModelPlacement.Local, "local/alias", "cloud/alias", null, Now);
        settings.ConfigureRole(ModelRole.ConversationResponse, false, ModelPlacement.Cloud, "local/alias", "cloud/alias", null, Now);
        rig.Gateway.Settings = settings;
        var versionBefore = (settings.UpdatedAtUtc, settings.IsEnabled, settings.AllowCloud);

        var local = await rig.Service.FindRoleTargetAsync(ModelRole.IntentInterpretation, CancellationToken.None);
        var cloud = await rig.Service.FindRoleTargetAsync(ModelRole.ConversationResponse, CancellationToken.None);
        var missing = await rig.Service.FindRoleTargetAsync(ModelRole.VisionCoverIdentification, CancellationToken.None);

        Assert.AreEqual(new ModelTestRoleTarget(ModelRole.IntentInterpretation, "local/alias", false), local);
        Assert.AreEqual(new ModelTestRoleTarget(ModelRole.ConversationResponse, "cloud/alias", true), cloud, "A disabled role can still be tested; cloud-ness comes from its placement.");
        Assert.IsNull(missing);
        Assert.AreEqual(versionBefore, (settings.UpdatedAtUtc, settings.IsEnabled, settings.AllowCloud));
    }

    [TestMethod]
    public void TheTestHarnessSeamIsReadOnly()
    {
        var methods = typeof(IModelTestTargetSource).GetMethods().Select(method => method.Name).Order().ToArray();

        CollectionAssert.AreEqual(ReadOnlyMethods, methods,
            "The test harness may look up a connection and a role's model; it has no way to change a role, switch, limit or permission.");
        Assert.IsFalse(typeof(ModelCapabilityTestService).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(IModelGatewaySettingsStore) || parameter.ParameterType == typeof(ModelGatewaySettingsService)),
            "The capability test is never given the gateway's write path.");
    }
}
