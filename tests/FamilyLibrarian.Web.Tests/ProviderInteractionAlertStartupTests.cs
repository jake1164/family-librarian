using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Web.Acquisition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ProviderInteractionAlertStartupTests
{
    [TestMethod]
    public void AnUnsetPublicOriginRegistersADisabledOptionsInstanceWithoutThrowing()
    {
        var services = new ServiceCollection();

        services.AddProviderInteractionAlertOptions(BuildConfiguration(new()), isDevelopment: false);

        var options = services.BuildServiceProvider().GetRequiredService<ProviderInteractionAlertOptions>();
        Assert.IsFalse(options.IsEnabled);
    }

    [TestMethod]
    public void AValidHttpsOriginListedInRemoteViewAllowedOriginsIsAccepted()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new()
        {
            ["Interaction:PublicOrigin"] = "https://fl.example.com",
            ["RemoteView:AllowedOrigins:0"] = "https://fl.example.com"
        });

        services.AddProviderInteractionAlertOptions(configuration, isDevelopment: false);

        var options = services.BuildServiceProvider().GetRequiredService<ProviderInteractionAlertOptions>();
        Assert.IsTrue(options.IsEnabled);
        Assert.AreEqual("https://fl.example.com", options.PublicOrigin);
    }

    [TestMethod]
    public void AnHttpOriginIsRejectedOutsideDevelopment()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Interaction:PublicOrigin"] = "http://fl.example.com",
            ["RemoteView:AllowedOrigins:0"] = "http://fl.example.com"
        });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => new ServiceCollection().AddProviderInteractionAlertOptions(configuration, isDevelopment: false));
    }

    [TestMethod]
    public void AnHttpOriginIsAcceptedInDevelopment()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new()
        {
            ["Interaction:PublicOrigin"] = "http://localhost:5000",
            ["RemoteView:AllowedOrigins:0"] = "http://localhost:5000"
        });

        services.AddProviderInteractionAlertOptions(configuration, isDevelopment: true);

        Assert.IsTrue(services.BuildServiceProvider()
            .GetRequiredService<ProviderInteractionAlertOptions>().IsEnabled);
    }

    [TestMethod]
    public void AnOriginNotListedInRemoteViewAllowedOriginsIsRejected()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Interaction:PublicOrigin"] = "https://fl.example.com",
            ["RemoteView:AllowedOrigins:0"] = "https://some-other-host.example.com"
        });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => new ServiceCollection().AddProviderInteractionAlertOptions(configuration, isDevelopment: false));
    }

    [TestMethod]
    public void AnOriginWithAPathIsRejected()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Interaction:PublicOrigin"] = "https://fl.example.com/interaction",
            ["RemoteView:AllowedOrigins:0"] = "https://fl.example.com"
        });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => new ServiceCollection().AddProviderInteractionAlertOptions(configuration, isDevelopment: false));
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
