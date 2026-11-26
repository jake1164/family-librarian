using FamilyLibrarian.Application.ChatAdministration;
using FamilyLibrarian.Contracts.Conversation;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Administrator-only switches for the chat assistant. The service checks for an administrator itself as well.</summary>
internal static class ChatSettingsEndpoints
{
    public static void MapChatSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/chat").RequireAuthorization("Admin").AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapGet("/settings", GetAsync);
        group.MapPut("/settings", SaveAsync);
    }

    private static async Task<IResult> GetAsync(ChatSettingsService service, CancellationToken cancellationToken) =>
        ToResult(await service.GetAsync(cancellationToken));

    private static async Task<IResult> SaveAsync(
        SetChatSettingsRequest request, ChatSettingsService service, CancellationToken cancellationToken)
    {
        if (request is { WebEnabled: null, MatrixEnabled: null })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Send at least one switch to change."] });
        }

        return ToResult(await service.UpdateAsync(request.WebEnabled, request.MatrixEnabled, cancellationToken));
    }

    private static IResult ToResult(ChatSettingsView? view) => view is null
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(new ChatSettingsResponse(view.WebEnabled, view.MatrixEnabled, view.MatrixConnectionReady));
}
