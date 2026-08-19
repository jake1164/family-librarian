using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Notifications;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Notifications;

public sealed class NotificationsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<IReadOnlyList<NotificationResponse>> GetAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<NotificationListResponse>(
            "api/v1/notifications/", cancellationToken);
        return response?.Notifications ?? [];
    }

    public Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        PostAsync($"api/v1/notifications/{notificationId}/read", cancellationToken);

    public Task DismissAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        PostAsync($"api/v1/notifications/{notificationId}/dismiss", cancellationToken);

    public Task DismissAllAsync(CancellationToken cancellationToken = default) =>
        PostAsync("api/v1/notifications/dismiss-all", cancellationToken);

    private async Task PostAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
