using System.Reflection;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTesting;

[TestClass]
public sealed class ModelOutputValidatorTests
{
    [TestMethod]
    [DataRow("{\"intent\":\"FindBook\"}", true)]
    [DataRow("{\"intent\":\"OutOfDomain\"}", true)]
    [DataRow(" {\"intent\" : \"RequestBook\"} ", true)]
    [DataRow("{\"intent\":\"findbook\"}", false)]
    [DataRow("{\"intent\":\"FindBook\",\"confidence\":0.9}", false)]
    [DataRow("{\"intent\":\"FindBook\",\"intent\":\"OutOfDomain\"}", false)]
    [DataRow("{\"Intent\":\"FindBook\"}", false)]
    [DataRow("```json\n{\"intent\":\"FindBook\"}\n```", false)]
    [DataRow("Sure: {\"intent\":\"FindBook\"}", false)]
    [DataRow("{\"intent\":null}", false)]
    [DataRow("{\"intent\":[\"FindBook\"]}", false)]
    [DataRow("", false)]
    [DataRow("null", false)]
    public void IntentRepliesAreAcceptedOnlyInTheExactShape(string reply, bool valid) =>
        Assert.AreEqual(valid, ModelOutputValidator.ValidateIntent(reply).IsValid, reply);

    [TestMethod]
    public void BookFactsNeedExactlyTheThreeTypedFields()
    {
        Assert.IsTrue(ModelOutputValidator.ValidateBookFacts("{\"title\":\"A\",\"author\":\"B\",\"year\":2019}").IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateBookFacts("{\"title\":\"A\",\"author\":\"B\",\"year\":\"2019\"}").IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateBookFacts("{\"title\":\"\",\"author\":\"B\",\"year\":2019}").IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateBookFacts("{\"title\":\"A\",\"author\":\"B\",\"year\":2019.5}").IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateBookFacts("{\"title\":\"A\",\"author\":\"B\",\"year\":2019,\"extra\":1}").IsValid);
    }

    [TestMethod]
    public void MatchAdvisoriesMustCiteOnlySuppliedEvidenceAndCarryNoConfidence()
    {
        string[] allowed = ["ev-1", "ev-2"];
        const string good = "{\"assessment\":\"likely_match\",\"reasons\":[{\"text\":\"Same title.\",\"evidence_id\":\"ev-1\"}]}";

        Assert.IsTrue(ModelOutputValidator.ValidateMatchAdvisory(good, allowed).IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateMatchAdvisory(good.Replace("ev-1", "ev-3", StringComparison.Ordinal), allowed).IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateMatchAdvisory(good.Replace("{\"assessment", "{\"confidence\":0.9,\"assessment", StringComparison.Ordinal), allowed).IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateMatchAdvisory("{\"assessment\":\"likely_match\",\"reasons\":[]}", allowed).IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateMatchAdvisory(good.Replace("likely_match", "definitely", StringComparison.Ordinal), allowed).IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateMatchAdvisory(
            "{\"assessment\":\"uncertain\",\"reasons\":[" + string.Join(",", Enumerable.Repeat("{\"text\":\"x\",\"evidence_id\":\"ev-1\"}", 4)) + "]}", allowed).IsValid);
    }

    [TestMethod]
    public void OversizedAndDeeplyNestedReplyIsRejectedWithoutThrowing()
    {
        Assert.IsFalse(ModelOutputValidator.ValidateIntent(new string(' ', ModelOutputValidator.MaxContentLength + 1) + "{}").IsValid);
        Assert.IsFalse(ModelOutputValidator.ValidateIntent(string.Concat(Enumerable.Repeat("[", 500))).IsValid);
    }
}

[TestClass]
public sealed class ModelTestSettingsServiceTests
{
    private static (ModelTestSettingsService Service, FakeSettingsStore Store, CapturingAudit Audit) Make(bool admin = true)
    {
        var store = new FakeSettingsStore();
        var audit = new CapturingAudit();
        return (new ModelTestSettingsService(store, new PassThroughProtector(), audit, new FakeUser(Guid.NewGuid()), new FakeAdmin(admin), new FakeClock()), store, audit);
    }

    private static SaveModelTestSettings Valid(bool allowCloud = false, string? cloud = null) =>
        new("http://litellm.example.test:4000/", "local/primary", "local/comparison", cloud, allowCloud, 30, 384);

    [TestMethod]
    public async Task FreshSettingsAreOffEmptyAndUseTheConservativeDefaults()
    {
        var (service, _, _) = Make();

        var result = await service.GetAsync(CancellationToken.None);

        var view = result.Settings!;
        Assert.IsFalse(view.IsEnabled);
        Assert.IsNull(view.EndpointUrl);
        Assert.IsNull(view.PrimaryModel);
        Assert.IsFalse(view.AllowCloud);
        Assert.IsFalse(view.HasApiKey);
        Assert.AreEqual(30, view.TimeoutSeconds);
        Assert.AreEqual(384, view.MaxOutputTokens);
    }

    [TestMethod]
    public async Task EveryOperationRefusesANonAdministratorAndChangesNothing()
    {
        var (service, store, audit) = Make(admin: false);

        var results = new[]
        {
            await service.GetAsync(CancellationToken.None),
            await service.SaveAsync(Valid(), CancellationToken.None),
            await service.SetEnabledAsync(true, CancellationToken.None),
            await service.SetApiKeyAsync("k", CancellationToken.None),
            await service.ClearApiKeyAsync(CancellationToken.None)
        };

        Assert.IsTrue(results.All(result => result.Forbidden));
        Assert.IsNull(store.Settings);
        Assert.AreEqual(0, store.Saves);
        Assert.IsEmpty(audit.Events);
    }

    [TestMethod]
    public async Task SavingTrimsTheEndpointAndNeverEnablesByItself()
    {
        var (service, store, _) = Make();

        var result = await service.SaveAsync(Valid(), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("http://litellm.example.test:4000", result.Settings!.EndpointUrl);
        Assert.IsFalse(store.Settings!.IsEnabled);
    }

    [TestMethod]
    public async Task TheApiKeyIsStoredProtectedNeverReturnedAndNeverAudited()
    {
        var (service, store, audit) = Make();

        var set = await service.SetApiKeyAsync("  sk-very-secret  ", CancellationToken.None);
        var read = await service.GetAsync(CancellationToken.None);

        Assert.IsTrue(set.Settings!.HasApiKey);
        Assert.AreEqual("p:sk-very-secret", store.Settings!.ProtectedApiKey, "Stored through the credential protector, trimmed.");
        Assert.IsTrue(read.Settings!.HasApiKey);
        Assert.IsFalse(typeof(ModelTestSettingsView).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.PropertyType == typeof(string) && property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)),
            "The view has no property that could carry the key.");
        Assert.IsFalse(audit.Events.Any(item => item.Json.Contains("secret", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("model_test.secret_set", audit.Events.Single().Action);

        var cleared = await service.ClearApiKeyAsync(CancellationToken.None);
        Assert.IsFalse(cleared.Settings!.HasApiKey);
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("   ", false)]
    public async Task ABlankApiKeyIsRejected(string key, bool valid)
    {
        var (service, _, _) = Make();

        Assert.AreEqual(valid, (await service.SetApiKeyAsync(key, CancellationToken.None)).Succeeded);
    }

    [TestMethod]
    [DataRow("ftp://host/")]
    [DataRow("not a url")]
    [DataRow("http://user:pass@host:4000")]
    [DataRow("http://host:4000/path?key=abc")]
    [DataRow("http://host:4000/#frag")]
    public async Task UnsafeEndpointsAreRejected(string endpoint)
    {
        var (service, store, _) = Make();

        var result = await service.SaveAsync(Valid() with { EndpointUrl = endpoint }, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(store.Settings);
    }

    [TestMethod]
    [DataRow("bad model name")]
    [DataRow("model;rm -rf")]
    [DataRow("<script>")]
    public async Task ModelAliasesWithUnusualCharactersAreRejected(string alias)
    {
        var (service, _, _) = Make();

        Assert.IsFalse((await service.SaveAsync(Valid() with { PrimaryModel = alias }, CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SaveAsync(Valid() with { ComparisonModel = alias }, CancellationToken.None)).Succeeded);
    }

    [TestMethod]
    public async Task AliasesLikeTheOwnersLiteLlmNamesAreAcceptedAsOrdinaryInput()
    {
        var (service, _, _) = Make();

        var result = await service.SaveAsync(
            Valid(allowCloud: true, cloud: "nemotron-3-nano:30b-cloud") with { PrimaryModel = "desktop/qwen3.5:9b", ComparisonModel = "desktop/gemma4:12b" },
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Error);
    }

    [TestMethod]
    public async Task LimitsAreEnforcedAndCloudNeedsAModelAlias()
    {
        var (service, _, _) = Make();

        Assert.IsFalse((await service.SaveAsync(Valid() with { TimeoutSeconds = 1 }, CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SaveAsync(Valid() with { TimeoutSeconds = 500 }, CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SaveAsync(Valid() with { MaxOutputTokens = 4 }, CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SaveAsync(Valid() with { MaxOutputTokens = 100_000 }, CancellationToken.None)).Succeeded);
        Assert.IsFalse((await service.SaveAsync(Valid(allowCloud: true, cloud: null), CancellationToken.None)).Succeeded);
        Assert.IsTrue((await service.SaveAsync(Valid(allowCloud: true, cloud: "cloud/model"), CancellationToken.None)).Succeeded);
    }

    [TestMethod]
    public async Task EnablingNeedsAnEndpointAndPrimaryModelAndClearingThemSwitchesTestingOff()
    {
        var (service, store, _) = Make();

        Assert.IsFalse((await service.SetEnabledAsync(true, CancellationToken.None)).Succeeded);
        await service.SaveAsync(Valid(), CancellationToken.None);
        Assert.IsTrue((await service.SetEnabledAsync(true, CancellationToken.None)).Succeeded);
        Assert.IsTrue(store.Settings!.IsEnabled);

        await service.SaveAsync(Valid() with { EndpointUrl = null }, CancellationToken.None);

        Assert.IsFalse(store.Settings.IsEnabled, "A configuration that can no longer run is not left enabled.");
    }
}

/// <summary>Pins the reach of the model test: it may configure, call a model and write audit records, and touch nothing else.</summary>
[TestClass]
public sealed class ModelTestingBoundaryTests
{
    private static readonly HashSet<Type> Allowed =
    [
        typeof(ICurrentUser), typeof(ICurrentAdministrator), typeof(IClock), typeof(IAuditWriter), typeof(ICredentialProtector)
    ];

    [TestMethod]
    public void EveryModelTestingTypeDependsOnlyOnTheAllowedServices()
    {
        var types = typeof(IModelClient).Assembly.GetTypes()
            .Where(type => type.Namespace == "FamilyLibrarian.Application.ModelTesting" && !type.IsNested && !type.IsInterface);

        foreach (var type in types)
        {
            foreach (var parameter in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic).SelectMany(constructor => constructor.GetParameters()))
            {
                var parameterType = parameter.ParameterType;
                Assert.IsTrue(
                    parameterType.Namespace == "FamilyLibrarian.Application.ModelTesting" || Allowed.Contains(parameterType) ||
                    parameterType.Namespace?.StartsWith("System", StringComparison.Ordinal) == true,
                    $"{type.Name} must not depend on {parameterType.FullName}: a model test result can never reach requests, matching, approvals or publishing.");
            }
        }
    }

    [TestMethod]
    public void TheModelSeamOffersCompletionOnly()
    {
        var methods = typeof(IModelClient).GetMethods().Select(method => method.Name).ToArray();

        Assert.AreEqual("CompleteAsync", string.Join(",", methods));
    }
}
