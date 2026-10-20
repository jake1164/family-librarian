using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.ModelGateway;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ModelGatewayFormTests
{
    [TestMethod]
    public void ClearingAnEndpointBackToItsOriginalEmptyValueIsClean()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: false, endpoint: null));
        form.Endpoint = "typed";
        Assert.IsTrue(form.HasUnsavedChanges);
        form.Endpoint = null!; // A cleared MudTextField can emit null.
        Assert.IsFalse(form.HasUnsavedChanges);
    }

    [TestMethod]
    public void AFreshlyLoadedFormHasNoUnsavedChanges()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: false));

        Assert.IsFalse(form.HasUnsavedChanges);
    }

    [TestMethod]
    public void ChoosingAModelIsAnUnsavedChange()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: true));

        form.Roles[0].IsEnabled = true;
        form.Roles[0].LocalModel = "local/model-a";

        Assert.IsTrue(form.HasUnsavedChanges);
    }

    [TestMethod]
    public void ChangingALimitIsAnUnsavedChange()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: true));

        form.Limits.Background.InstallationDailyRequests = 7;

        Assert.IsTrue(form.HasUnsavedChanges);
    }

    // The defect this guards: choosing models and then flipping the master switch (or storing a key) reloaded the page
    // from the host and silently discarded the choices, which had never been saved.
    [TestMethod]
    public void TurningAiOnKeepsModelChoicesThatWereNotSavedYet()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: false));
        form.Roles[0].IsEnabled = true;
        form.Roles[0].LocalModel = "local/model-a";
        form.Roles[0].Reasoning = "Disabled";

        var kept = form.Refresh(Settings(isEnabled: true));

        Assert.IsTrue(kept);
        Assert.IsTrue(form.Roles[0].IsEnabled);
        Assert.AreEqual("local/model-a", form.Roles[0].LocalModel);
        Assert.AreEqual("Disabled", form.Roles[0].Reasoning);
        Assert.IsTrue(form.HasUnsavedChanges, "Kept edits are still unsaved until the administrator saves them.");
    }

    [TestMethod]
    public void StoringAKeyKeepsUnsavedEndpointAndLimitEdits()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: true));
        form.Endpoint = "http://example-gateway:4000";
        form.Limits.TimeoutSeconds = 60;

        var kept = form.Refresh(Settings(isEnabled: true, hasApiKey: true));

        Assert.IsTrue(kept);
        Assert.AreEqual("http://example-gateway:4000", form.Endpoint);
        Assert.AreEqual(60, form.Limits.TimeoutSeconds);
    }

    [TestMethod]
    public void WithNothingUnsavedARefreshFollowsTheHost()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: false));

        var kept = form.Refresh(Settings(isEnabled: true, endpoint: "http://example-gateway:4000"));

        Assert.IsFalse(kept);
        Assert.AreEqual("http://example-gateway:4000", form.Endpoint);
        Assert.IsFalse(form.HasUnsavedChanges);
    }

    [TestMethod]
    public void LoadingAfterASaveClearsUnsavedChanges()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: true));
        form.Roles[0].IsEnabled = true;
        form.Roles[0].LocalModel = "local/model-a";

        var saved = form.ToRequest();
        form.Load(Settings(isEnabled: true) with { Roles = saved.Roles });

        Assert.IsFalse(form.HasUnsavedChanges);
        Assert.AreEqual("local/model-a", form.Roles[0].LocalModel);
    }

    [TestMethod]
    public void TheSaveRequestCarriesEveryEditedField()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: true));
        form.Endpoint = "http://example-gateway:4000";
        form.Roles[1].IsEnabled = true;
        form.Roles[1].LocalModel = "local/model-b";
        form.Limits.MaxOutputTokens = 300;

        var request = form.ToRequest();

        Assert.AreEqual("http://example-gateway:4000", request.EndpointUrl);
        Assert.IsTrue(request.Roles[1].IsEnabled);
        Assert.AreEqual("local/model-b", request.Roles[1].LocalModel);
        Assert.AreEqual(300, request.Limits!.MaxOutputTokens);
    }

    [TestMethod]
    public void CloudExcerptsAreNeverRequestedWithoutCloudUse()
    {
        var form = new ModelGatewayForm();
        form.Load(Settings(isEnabled: true));
        form.AllowCloud = false;
        form.AllowCloudExcerpts = true;

        Assert.IsFalse(form.ToRequest().AllowCloudExcerpts);
    }

    private static ModelGatewaySettingsResponse Settings(bool isEnabled, bool hasApiKey = false, string? endpoint = "http://example-gateway:4000/v1") => new(
        isEnabled,
        endpoint,
        hasApiKey,
        hasApiKey ? DateTimeOffset.Parse("2026-10-14T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture) : null,
        AllowCloud: false,
        Roles:
        [
            new ModelRoleDto("IntentInterpretation", false, "Local", null, null),
            new ModelRoleDto("CandidateAmbiguityAssessment", false, "Local", null, null)
        ],
        Limits: new ModelLimitsDto(30, 12000, 512, 2, new ModelBudgetDto(30, 60000, 300, 600000), new ModelBudgetDto(0, 0, 200, 400000), 0, 0));
}
