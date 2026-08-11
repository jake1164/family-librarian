using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Integrations;

namespace FamilyLibrarian.Infrastructure.Tests.Integrations;

[TestClass]
public sealed class MetadataProviderStateTests
{
    [TestMethod]
    public void TheDeploymentDefaultAppliesUntilAnAdministratorSavesASetting()
    {
        var descriptor = Keyless(defaultEnabled: true);

        Assert.IsTrue(MetadataProviderState.IsEnabled(descriptor, setting: null));
    }

    [TestMethod]
    public void AStoredSettingOverridesTheDeploymentDefault()
    {
        var descriptor = Keyless(defaultEnabled: true);
        var setting = new MetadataProviderSetting("openlibrary", DateTimeOffset.UnixEpoch);
        setting.SetEnabled(false, actorUserId: null, DateTimeOffset.UnixEpoch);

        Assert.IsFalse(MetadataProviderState.IsEnabled(descriptor, setting));
    }

    [TestMethod]
    public void AnExternallyManagedProviderFollowsConfigurationAndIgnoresTheStoredRow()
    {
        var descriptor = new MetadataProviderDescriptor(
            "googlebooks",
            "Google Books",
            RequiresCredential: true,
            HasExternallyManagedCredential: true,
            DefaultEnabled: true);

        var setting = new MetadataProviderSetting("googlebooks", DateTimeOffset.UnixEpoch);
        setting.SetEnabled(false, actorUserId: null, DateTimeOffset.UnixEpoch);

        Assert.IsTrue(MetadataProviderState.IsEnabled(descriptor, setting));
    }

    [TestMethod]
    public void AnEnabledCredentialedProviderWithNoKeyIsNotUsable()
    {
        var descriptor = new MetadataProviderDescriptor(
            "googlebooks",
            "Google Books",
            RequiresCredential: true,
            HasExternallyManagedCredential: false,
            DefaultEnabled: true);

        Assert.IsTrue(MetadataProviderState.IsEnabled(descriptor, setting: null));
        Assert.IsFalse(MetadataProviderState.IsUsable(descriptor, setting: null));
    }

    [TestMethod]
    public void AKeylessProviderIsUsableWhenEnabled()
    {
        var descriptor = Keyless(defaultEnabled: true);

        Assert.IsTrue(MetadataProviderState.IsUsable(descriptor, setting: null));
    }

    private static MetadataProviderDescriptor Keyless(bool defaultEnabled) => new(
        "openlibrary",
        "Open Library",
        RequiresCredential: false,
        HasExternallyManagedCredential: false,
        DefaultEnabled: defaultEnabled);
}
