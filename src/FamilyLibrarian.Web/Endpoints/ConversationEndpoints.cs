using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Contracts.Conversation;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// The web chat (alpha.6 M3-3). The same <see cref="IConversationService"/> the Matrix adapter uses: this class only maps
/// HTTP to a turn and a reply back to JSON. Any signed-in member may chat; there is no administrative action in the engine,
/// and the caller's identity is the signed-in cookie principal, never anything in the body.
/// </summary>
internal static class ConversationEndpoints
{
    public const string RateLimitPolicy = "conversation-messages";
    public const string WebScopeKey = "web";
    private const int MaxMessageIdLength = 100;

    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var conversation = app.MapGroup("/api/v1/conversation")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        conversation.MapGet("/status", GetStatus);
        conversation.MapPost("/messages", SendMessageAsync).RequireRateLimiting(RateLimitPolicy);
    }

    private static IResult GetStatus(ConversationOptions options) =>
        Results.Ok(new ConversationStatusResponse(options.IsEnabledFor(ConversationTransports.Web), options.MaxInputCharacters));

    private static async Task<IResult> SendMessageAsync(
        ConversationMessageRequest request,
        ConversationOptions options,
        IConversationService conversation,
        CancellationToken cancellationToken)
    {
        // A feature that is off does not exist: same answer as an unknown route.
        if (!options.IsEnabledFor(ConversationTransports.Web))
        {
            return Results.NotFound();
        }

        var messageId = string.IsNullOrWhiteSpace(request.MessageId) ? null : request.MessageId.Trim();
        if (messageId is { Length: > MaxMessageIdLength })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["messageId"] = ["The message id is too long."] });
        }

        var result = await conversation.HandleAsync(
            new ConversationTurn(ConversationTransports.Web, WebScopeKey, messageId, request.Text ?? string.Empty), cancellationToken);

        var response = ToResponse(result);
        return result.Reply.Kind switch
        {
            ConversationReplyKind.Unauthenticated => Results.Unauthorized(),
            ConversationReplyKind.RateLimited => Results.Json(response, statusCode: StatusCodes.Status429TooManyRequests),
            ConversationReplyKind.Disabled => Results.NotFound(),
            _ => Results.Ok(response)
        };
    }

    internal static ConversationReplyResponse ToResponse(ConversationTurnResult result) => new(
        result.Reply.Kind.ToString(),
        result.Reply.Text,
        result.Reply.Choices.Select(choice => new ConversationChoiceResponse(choice.Number, choice.Title, choice.Authors, choice.Detail)).ToArray(),
        result.Reply.Requests.Select(request => new ConversationRequestSummaryResponse(
            request.RequestId, request.Title, request.Authors, request.Status, request.Formats)).ToArray(),
        result.Reply.SuggestedReplies,
        result.Reply.RequestId,
        result.Replayed);
}
