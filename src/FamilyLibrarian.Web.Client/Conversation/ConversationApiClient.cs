using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Conversation;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Conversation;

/// <summary>Typed client for the web chat. Every call is the signed-in member's own; the host never trusts anything but the cookie.</summary>
public sealed class ConversationApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/conversation";

    public async Task<ConversationStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ConversationStatusResponse>($"{BasePath}/status", cancellationToken)
        ?? new ConversationStatusResponse(false, 0);

    /// <summary>
    /// Sends one message. <paramref name="messageId"/> makes a retry of the same message harmless. Null when the chat is
    /// switched off or the member is no longer signed in.
    /// </summary>
    public async Task<ConversationReplyResponse?> SendAsync(string text, string messageId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/messages")
        {
            Content = JsonContent.Create(new ConversationMessageRequest(text, messageId))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        // 429 carries the same body shape (a "slow down" reply), so the page can show it like any other answer.
        return await response.Content.ReadFromJsonAsync<ConversationReplyResponse>(cancellationToken);
    }
}
