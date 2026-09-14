using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Infrastructure.Communications;

/// <summary>
/// A thin client over the Matrix Client-Server API v3, the same
/// raw-<see cref="IHttpClientFactory"/> style as <c>CwaCatalogClient</c>/
/// <c>AudiobookshelfApiClient</c> rather than a third-party Matrix SDK -- no
/// such SDK exists in this codebase's dependency set, and every other
/// admin-configured external service here is integrated the same way.
/// Response parsing is deliberately defensive (<see cref="JsonNode"/> rather
/// than a strict DTO), matching <c>AudiobookshelfApiClient</c>'s own
/// documented rationale: a shape mismatch degrades to a failure result, not
/// an exception. Not yet live-verified against a real homeserver -- same
/// disclosed gap as every other admin-configured HTTP integration in this
/// codebase before its first live pass.
/// </summary>
public sealed class HttpMatrixClient(IHttpClientFactory httpClientFactory) : IMatrixClient
{
    public async Task<ConnectionTestOutcome> TestConnectionAsync(
        MatrixSettings settings, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(settings, accessToken);
            using var response = await client.GetAsync("_matrix/client/v3/account/whoami", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionTestOutcome(false, await DescribeErrorAsync(response, cancellationToken));
            }

            var body = await ReadJsonAsync(response, cancellationToken);
            var userId = body?["user_id"]?.GetValue<string>();
            return new ConnectionTestOutcome(
                true, userId is null ? "Connected." : $"Connected as {userId}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ConnectionTestOutcome(false, $"Could not reach the Matrix homeserver: {exception.Message}");
        }
    }

    public async Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
        MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(settings, accessToken);
            var payload = new JsonObject
            {
                ["invite"] = new JsonArray(matrixUserId),
                ["is_direct"] = true,
                ["preset"] = "trusted_private_chat",
            };

            using var response = await client.PostAsJsonAsync("_matrix/client/v3/createRoom", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return MatrixRoomResult.Failure(await DescribeErrorAsync(response, cancellationToken));
            }

            var body = await ReadJsonAsync(response, cancellationToken);
            var roomId = body?["room_id"]?.GetValue<string>();
            return roomId is null
                ? MatrixRoomResult.Failure("The homeserver did not return a room id.")
                : MatrixRoomResult.Success(roomId);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return MatrixRoomResult.Failure($"Could not reach the Matrix homeserver: {exception.Message}");
        }
    }

    public async Task<SendResult> SendMessageAsync(
        MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(settings, accessToken);
            var payload = new JsonObject
            {
                ["msgtype"] = "m.text",
                ["body"] = text,
            };

            var transactionId = Guid.NewGuid().ToString("N");
            var requestUri = $"_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{transactionId}";
            using var response = await client.PutAsJsonAsync(requestUri, payload, cancellationToken);
            return response.IsSuccessStatusCode
                ? SendResult.Success()
                : SendResult.Failure(await DescribeErrorAsync(response, cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Failure($"Could not reach the Matrix homeserver: {exception.Message}");
        }
    }

    public async Task<MatrixSyncResult> SyncAsync(
        MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(settings, accessToken);
            client.Timeout = TimeSpan.FromSeconds(40);

            var timeoutMs = since is null ? 0 : 25_000;
            var requestUri = $"_matrix/client/v3/sync?timeout={timeoutMs}"
                + (since is null ? "" : $"&since={Uri.EscapeDataString(since)}");
            using var response = await client.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return MatrixSyncResult.Failure(await DescribeErrorAsync(response, cancellationToken));
            }

            var body = await ReadJsonAsync(response, cancellationToken);
            var nextBatch = body?["next_batch"]?.GetValue<string>();
            var botUserId = settings.BotUserId;
            var messages = new List<MatrixInboundMessage>();

            if (body?["rooms"]?["join"] is JsonObject joinedRooms)
            {
                foreach (var (roomId, room) in joinedRooms)
                {
                    if (room?["timeline"]?["events"] is not JsonArray events) continue;
                    foreach (var candidateEvent in events)
                    {
                        if (candidateEvent?["type"]?.GetValue<string>() != "m.room.message") continue;
                        var sender = candidateEvent["sender"]?.GetValue<string>();
                        var messageBody = candidateEvent["content"]?["body"]?.GetValue<string>();
                        var msgType = candidateEvent["content"]?["msgtype"]?.GetValue<string>();
                        if (sender is null || messageBody is null || msgType != "m.text") continue;
                        // Never react to the bot's own messages (verification codes, replies) echoed back through /sync.
                        if (botUserId is not null && string.Equals(sender, botUserId, StringComparison.Ordinal)) continue;

                        messages.Add(new MatrixInboundMessage(roomId, sender, messageBody));
                    }
                }
            }

            return MatrixSyncResult.Success(nextBatch, messages);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return MatrixSyncResult.Failure($"Could not reach the Matrix homeserver: {exception.Message}");
        }
    }

    private HttpClient CreateClient(MatrixSettings settings, string accessToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri($"{settings.HomeserverUrl!.TrimEnd('/')}/");
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(body) as JsonObject;
    }

    private static async Task<string> DescribeErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadJsonAsync(response, cancellationToken);
            var errcode = body?["errcode"]?.GetValue<string>();
            var error = body?["error"]?.GetValue<string>();
            if (error is not null)
            {
                return errcode is null ? error : $"{errcode}: {error}";
            }
        }
        catch (Exception)
        {
            // Fall through to the generic status-based message below.
        }

        return $"The Matrix homeserver returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }
}
