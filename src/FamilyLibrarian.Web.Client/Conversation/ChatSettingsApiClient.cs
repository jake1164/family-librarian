using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Conversation;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Conversation;

/// <summary>Typed client for the administrator-only chat switches.</summary>
public sealed class ChatSettingsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string SettingsPath = "api/v1/admin/chat/settings";

    public async Task<ChatSettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ChatSettingsResponse>(SettingsPath, cancellationToken)
        ?? throw new HttpRequestException("The host returned no chat settings.");

    /// <summary>Null when the host did not save the change.</summary>
    public async Task<ChatSettingsResponse?> SaveAsync(SetChatSettingsRequest change, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, SettingsPath) { Content = JsonContent.Create(change) };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ChatSettingsResponse>(cancellationToken)
            : null;
    }
}
