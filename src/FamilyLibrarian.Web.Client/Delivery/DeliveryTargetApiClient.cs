using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Delivery;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Delivery;

/// <summary>Typed client for the current user's Kindle delivery settings.</summary>
public sealed class DeliveryTargetApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<DeliveryTargetResponse?> GetMyKindleTargetAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/me/delivery/kindle", cancellationToken);

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await response.Content.ReadFromJsonAsync<DeliveryTargetResponse>(cancellationToken);
    }

    public async Task<SetKindleTargetOutcome> SetKindleAddressAsync(
        string address,
        uint? expectedVersion,
        bool sendByDefault,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            "api/v1/me/delivery/kindle",
            new SetKindleAddressRequest(address, expectedVersion, sendByDefault),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new SetKindleTargetOutcome(
                true,
                await response.Content.ReadFromJsonAsync<DeliveryTargetResponse>(cancellationToken),
                null)
            : new SetKindleTargetOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
    }

    public async Task<SetKindleTargetOutcome> SetKindleEnabledAsync(
        bool enabled,
        uint expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            "api/v1/me/delivery/kindle/enabled",
            new SetKindleEnabledRequest(enabled, expectedVersion),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new SetKindleTargetOutcome(
                true,
                await response.Content.ReadFromJsonAsync<DeliveryTargetResponse>(cancellationToken),
                null)
            : new SetKindleTargetOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
    }

    public async Task<TestKindleDeliveryResponse?> TestKindleDeliveryAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/me/delivery/kindle/test");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TestKindleDeliveryResponse>(cancellationToken)
            : null;
    }

    public async Task<SendExistingBookResponse?> SendExistingBookAsync(
        Guid workId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/me/delivery/kindle/send-existing",
            new SendExistingBookRequest(workId),
            cancellationToken);

        // Both a success and a "not owned"/"not configured" 404 carry a body
        // with the message to show the user -- only 401 (no session) has none.
        return response.StatusCode == HttpStatusCode.Unauthorized
            ? null
            : await response.Content.ReadFromJsonAsync<SendExistingBookResponse>(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload payload,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var response = await SendOnceAsync(method, path, payload, cancellationToken);

        // A token that outlived its identity cookie reads as a bad request. Retry
        // once with a fresh one before showing the user an error they cannot act on.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            antiforgery.Invalidate();
            var retry = await SendOnceAsync(method, path, payload, cancellationToken);
            if (retry.IsSuccessStatusCode)
            {
                response.Dispose();
                return retry;
            }

            retry.Dispose();
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload payload,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload)
        };

        await antiforgery.AttachAsync(request, cancellationToken);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "Add a Kindle address first.";
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return "This has changed since you loaded it. Reload and try again.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(
                cancellationToken);
            var message = problem?.Errors?.Values
                .SelectMany(messages => messages)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // Fall through to the generic message below.
        }

        return "That change could not be saved. Please try again.";
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record SetKindleTargetOutcome(bool Succeeded, DeliveryTargetResponse? Target, string? Error);
