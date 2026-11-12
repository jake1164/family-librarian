using FamilyLibrarian.Application.Acquisition;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class ScopedSavedAcquisitionQuestionAcceptor(IServiceScopeFactory scopes) : ISavedAcquisitionQuestionAcceptor
{
    public async Task<bool> TryAcceptAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AutomaticRequestFulfillmentService>()
            .AcceptBySavedPreferencesAsync(requestId, cancellationToken);
    }
}
