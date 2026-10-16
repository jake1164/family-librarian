using FamilyLibrarian.Application.Communications;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Communications;

public sealed class ScopedAcquisitionQuestionMatrixAnswers(IServiceScopeFactory scopes) : IAcquisitionQuestionMatrixAnswers
{
    public async Task<string?> AnswerAsync(MatrixInboundMessage message, string body, CancellationToken cancellationToken)
    {
        if (!AcquisitionQuestionAnswerGrammar.IsKeyword(body) && body is not ("1" or "2")) return null;
        await using var scope = scopes.CreateAsyncScope();
        var authenticated = await scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>().AuthenticateAsync(message, cancellationToken);
        if (!authenticated.IsAuthenticated) return AcquisitionQuestionAnswerGrammar.IsKeyword(body) ? "I couldn't authorize that answer. Open Family Librarian to review the request." : null;
        return await scope.ServiceProvider.GetRequiredService<AcquisitionQuestionMatrixAnswerService>().AnswerAsync(message, body, cancellationToken);
    }
}
