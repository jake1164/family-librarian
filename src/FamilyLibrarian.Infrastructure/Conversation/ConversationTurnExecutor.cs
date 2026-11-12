using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Conversation;

public sealed partial class ConversationTurnExecutor(
    IServiceScopeFactory scopes, ConversationOptions options, ILogger<ConversationTurnExecutor> logger) : IConversationTurnExecutor
{
    public async Task<ConversationTurnResult?> RunMatrixTurnAsync(MatrixInboundMessage message, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // No event id means no way to make a replay harmless; real Matrix events always have one.
        if (!options.IsEnabledFor(ConversationTransports.Matrix) || string.IsNullOrWhiteSpace(message.EventId))
        {
            return null;
        }

        try
        {
            // A scope per message: the principal lives and dies with it.
            await using var scope = scopes.CreateAsyncScope();
            var authenticated = await scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>()
                .AuthenticateAsync(message, cancellationToken);
            if (!authenticated.IsAuthenticated)
            {
                LogNotAuthenticated(authenticated.Outcome);
                return null;
            }

            return await scope.ServiceProvider.GetRequiredService<IConversationService>().HandleAsync(
                new ConversationTurn(ConversationTransports.Matrix, message.RoomId, message.EventId, text), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One bad message must never wedge the sync loop. Nothing was recorded, so a retry by the user runs normally.
            LogTurnFailed(exception);
            return new ConversationTurnResult(
                ConversationReply.Of(ConversationReplyKind.Unavailable, "Something went wrong on my side. Please try that again in a moment."),
                Replayed: false, DeliveryPending: false, EventKey: null);
        }
    }

    public async Task MarkDeliveredAsync(ConversationEventKey key, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IConversationService>().MarkDeliveredAsync(key, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Worst case the reply is offered again on a replay of the same event.
            LogTurnFailed(exception);
        }
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Matrix conversation message was not authenticated ({Outcome}).")]
    private partial void LogNotAuthenticated(MatrixAuthenticationOutcome outcome);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "Matrix conversation turn failed.")]
    private partial void LogTurnFailed(Exception exception);
}
