using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Integrations;

/// <summary>
/// Supplies a provider credential to components that outlive a request scope.
/// </summary>
/// <remarks>
/// <see cref="MetadataCredentialSource"/> is scoped because it reads the
/// database. Pooled <c>DelegatingHandler</c> instances are not, so they depend on
/// this singleton-safe accessor instead of the scoped type directly.
/// </remarks>
public interface IMetadataCredentialAccessor
{
    Task<string?> GetCredentialAsync(string providerId, CancellationToken cancellationToken);
}

/// <summary>Opens a scope per call so the scoped credential source can be resolved.</summary>
public sealed class ScopedMetadataCredentialAccessor(IServiceScopeFactory scopeFactory)
    : IMetadataCredentialAccessor
{
    public async Task<string?> GetCredentialAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<MetadataCredentialSource>();
        return await source.GetCredentialAsync(providerId, cancellationToken);
    }
}
