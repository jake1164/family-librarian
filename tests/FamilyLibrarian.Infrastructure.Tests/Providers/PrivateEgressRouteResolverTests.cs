using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class PrivateEgressRouteResolverTests
{
    [TestMethod]
    public void NormalIsAlwaysAllowedDirectRegardlessOfGatewayState()
    {
        var resolver = new PrivateEgressRouteResolver(new FakeGatewayCache(PrivateEgressGatewayRuntimeState.Disabled));

        var resolution = resolver.Resolve(EgressPolicy.Normal);

        Assert.IsTrue(resolution.IsAllowed);
        Assert.AreEqual(EgressRoute.Direct, resolution.Route);
    }

    [TestMethod]
    public void PrivateRequiredIsBlockedWhenTheGatewayIsDisabled()
    {
        var resolver = new PrivateEgressRouteResolver(new FakeGatewayCache(PrivateEgressGatewayRuntimeState.Disabled));

        var resolution = resolver.Resolve(EgressPolicy.PrivateRequired);

        Assert.IsFalse(resolution.IsAllowed);
        Assert.IsNotNull(resolution.BlockedReason);
    }

    [TestMethod]
    public void PrivateRequiredIsBlockedWhenTheLastTestFailed()
    {
        var resolver = new PrivateEgressRouteResolver(
            new FakeGatewayCache(new PrivateEgressGatewayRuntimeState(true, "http://gateway:8888", LastTestSucceeded: false)));

        var resolution = resolver.Resolve(EgressPolicy.PrivateRequired);

        Assert.IsFalse(resolution.IsAllowed);
    }

    [TestMethod]
    public void CustomProxyIsBlockedWhenNoEndpointIsConfigured()
    {
        var resolver = new PrivateEgressRouteResolver(
            new FakeGatewayCache(new PrivateEgressGatewayRuntimeState(true, GatewayEndpoint: null, LastTestSucceeded: true)));

        var resolution = resolver.Resolve(EgressPolicy.CustomProxy);

        Assert.IsFalse(resolution.IsAllowed);
    }

    [TestMethod]
    public void PrivateRequiredIsAllowedViaTheGatewayWhenEnabledAndLastTestedSuccessfully()
    {
        var resolver = new PrivateEgressRouteResolver(
            new FakeGatewayCache(new PrivateEgressGatewayRuntimeState(true, "http://gateway:8888", LastTestSucceeded: true)));

        var resolution = resolver.Resolve(EgressPolicy.PrivateRequired);

        Assert.IsTrue(resolution.IsAllowed);
        var gatewayRoute = Assert.IsInstanceOfType<EgressRoute.GatewayRoute>(resolution.Route);
        Assert.AreEqual(new Uri("http://gateway:8888"), gatewayRoute.ProxyEndpoint);
    }

    private sealed class FakeGatewayCache(PrivateEgressGatewayRuntimeState state) : IPrivateEgressGatewayRuntimeCache
    {
        public PrivateEgressGatewayRuntimeState Current => state;

        public void Refresh(PrivateEgressGatewayRuntimeState newState) => throw new NotSupportedException();
    }
}
