using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ProviderStateTests
{
    [TestMethod]
    public void TheDeploymentDefaultAppliesUntilAnAdministratorSavesASetting()
    {
        var descriptor = Keyless(defaultEnabled: true);

        Assert.IsTrue(ProviderState.IsEnabled(descriptor, setting: null));
    }

    [TestMethod]
    public void AStoredSettingOverridesTheDeploymentDefault()
    {
        var descriptor = Keyless(defaultEnabled: true);
        var setting = new ProviderSetting("openlibrary", DateTimeOffset.UnixEpoch);
        setting.SetEnabled(false, actorUserId: null, DateTimeOffset.UnixEpoch);

        Assert.IsFalse(ProviderState.IsEnabled(descriptor, setting));
    }

    [TestMethod]
    public void AnExternallyManagedProviderFollowsConfigurationAndIgnoresTheStoredRow()
    {
        var descriptor = new ProviderDescriptor(
            "googlebooks",
            "Google Books",
            MetadataOnly,
            RequiresCredential: true,
            HasExternallyManagedCredential: true,
            DefaultEnabled: true);

        var setting = new ProviderSetting("googlebooks", DateTimeOffset.UnixEpoch);
        setting.SetEnabled(false, actorUserId: null, DateTimeOffset.UnixEpoch);

        Assert.IsTrue(ProviderState.IsEnabled(descriptor, setting));
    }

    [TestMethod]
    public void AnEnabledCredentialedProviderWithNoKeyIsNotUsable()
    {
        var descriptor = new ProviderDescriptor(
            "googlebooks",
            "Google Books",
            MetadataOnly,
            RequiresCredential: true,
            HasExternallyManagedCredential: false,
            DefaultEnabled: true);

        Assert.IsTrue(ProviderState.IsEnabled(descriptor, setting: null));
        Assert.IsFalse(ProviderState.IsUsable(descriptor, setting: null));
    }

    [TestMethod]
    public void AKeylessProviderIsUsableWhenEnabled()
    {
        var descriptor = Keyless(defaultEnabled: true);

        Assert.IsTrue(ProviderState.IsUsable(descriptor, setting: null));
    }

    private static readonly IReadOnlySet<ProviderCapability> MetadataOnly =
        new HashSet<ProviderCapability> { ProviderCapability.Metadata };

    private static ProviderDescriptor Keyless(bool defaultEnabled) => new(
        "openlibrary",
        "Open Library",
        MetadataOnly,
        RequiresCredential: false,
        HasExternallyManagedCredential: false,
        DefaultEnabled: defaultEnabled);
}
