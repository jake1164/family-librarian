using System.Security.Claims;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Contracts.Notifications;

namespace FamilyLibrarian.Web.Endpoints;

internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // Every authenticated user has a notification feed — admins additionally
        // see the broadcast entries. The role check happens inside each handler
        // rather than on the group, since (unlike AdminRequestEndpoints) this
        // group is not admin-only.
        var notifications = app.MapGroup("/api/v1/notifications")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        notifications.MapGet("/", ListNotificationsAsync);
        notifications.MapPost("/{notificationId:guid}/read", MarkReadAsync);
        notifications.MapPost("/{notificationId:guid}/dismiss", DismissAsync);
        notifications.MapPost("/dismiss-all", DismissAllAsync);
    }

    private static async Task<IResult> ListNotificationsAsync(
        ClaimsPrincipal user,
        NotificationService notifications,
        CancellationToken cancellationToken)
    {
        var views = await notifications.ListForViewerAsync(user.IsInRole("Admin"), cancellationToken);
        return Results.Ok(new NotificationListResponse(views.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> MarkReadAsync(
        Guid notificationId,
        NotificationService notifications,
        CancellationToken cancellationToken)
    {
        await notifications.MarkReadAsync(notificationId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DismissAsync(
        Guid notificationId,
        NotificationService notifications,
        CancellationToken cancellationToken)
    {
        await notifications.DismissAsync(notificationId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DismissAllAsync(
        ClaimsPrincipal user,
        NotificationService notifications,
        CancellationToken cancellationToken)
    {
        await notifications.DismissAllAsync(user.IsInRole("Admin"), cancellationToken);
        return Results.NoContent();
    }

    private static NotificationResponse ToResponse(NotificationView view) => new(
        view.Id,
        view.Category,
        view.Severity.ToString(),
        view.Title,
        view.Detail,
        view.SubjectType,
        view.SubjectId,
        view.RepeatCount,
        view.OccurredAtUtc,
        view.LastOccurredAtUtc,
        view.IsRead);
}
