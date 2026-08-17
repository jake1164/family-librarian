using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class SecurityPipelineStartupCheckTests
{
    [TestMethod]
    public void ThrowsWhenNoAssetValidatorIsRegistered()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => services.EnsureAssetValidatorsAreConfigured());
    }

    [TestMethod]
    public void DoesNotThrowWhenAtLeastOneAssetValidatorIsRegistered()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAssetValidator, AlwaysPassesValidator>()
            .BuildServiceProvider();

        services.EnsureAssetValidatorsAreConfigured();
    }

    private sealed class AlwaysPassesValidator : IAssetValidator
    {
        public string Id => "always-passes";

        public Task<ValidationOutcome> ValidateAsync(
            MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(new ValidationOutcome(true, null));
    }
}
