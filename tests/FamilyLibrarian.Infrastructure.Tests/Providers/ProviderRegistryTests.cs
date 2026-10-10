using FamilyLibrarian.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// Built-in sources that call a public third-party service are on by default,
/// but a deployment (and the integration lab, which must not depend on public
/// sites) has to be able to turn them off at boot. A change made through the
/// API after startup is too late: the Project Gutenberg catalogue import begins
/// as soon as the host starts.
/// </summary>
[TestClass]
public sealed class ProviderRegistryTests
{
    [TestMethod]
    [DataRow(ProviderRegistry.GutenbergProviderId, "MetadataProviders:Gutenberg:Enabled")]
    [DataRow(ProviderRegistry.LibriVoxProviderId, "MetadataProviders:LibriVox:Enabled")]
    public void APublicBuiltInSourceIsEnabledByDefault(string providerId, string _)
    {
        var registry = new ProviderRegistry(new ConfigurationBuilder().Build());

        Assert.IsTrue(registry.Find(providerId)!.DefaultEnabled);
    }

    [TestMethod]
    [DataRow(ProviderRegistry.GutenbergProviderId, "MetadataProviders:Gutenberg:Enabled")]
    [DataRow(ProviderRegistry.LibriVoxProviderId, "MetadataProviders:LibriVox:Enabled")]
    public void ADeploymentCanDisableAPublicBuiltInSourceAtBoot(string providerId, string settingKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [settingKey] = "false" })
            .Build();

        var registry = new ProviderRegistry(configuration);

        Assert.IsFalse(registry.Find(providerId)!.DefaultEnabled);
    }
}
