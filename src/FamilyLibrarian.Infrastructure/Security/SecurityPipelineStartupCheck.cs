using FamilyLibrarian.Application.Security;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Security;

public static class SecurityPipelineStartupCheck
{
    /// <summary>
    /// Refuses to start rather than silently accept every uploaded or acquired
    /// file as valid.
    /// </summary>
    /// <remarks>
    /// <see cref="SecurityEvaluationService"/> iterates whatever
    /// <see cref="IAssetValidator"/> instances are registered; an empty
    /// collection is not a "no validators configured" state distinguishable
    /// from "every validator passed" — the loop simply never runs, and
    /// <see cref="Domain.Security.SecurityEvaluation.Evaluate"/> sees no
    /// invalid-format result to fail on. Malware scanning still runs
    /// independently, so this is specifically about the second, currently
    /// unimplemented half of the security gate — see F1 in the architecture
    /// review and <c>docs/03-provider-api-contracts.md</c> §8, which names
    /// <c>FileTypeValidator</c>, <c>EpubValidator</c>, and <c>AudioValidator</c>
    /// as the initial validators none of which exist yet.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <see cref="IAssetValidator"/> is registered.</exception>
    public static void EnsureAssetValidatorsAreConfigured(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var validators = scope.ServiceProvider.GetServices<IAssetValidator>();

        if (!validators.Any())
        {
            throw new InvalidOperationException(
                "No IAssetValidator is registered. The security gate requires at least one " +
                "format validator before this deployment can accept manually imported or " +
                "acquired files — see docs/03-provider-api-contracts.md §8.");
        }
    }
}
