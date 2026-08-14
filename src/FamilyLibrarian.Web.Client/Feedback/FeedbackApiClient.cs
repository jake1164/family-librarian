using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Feedback;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Feedback;

/// <summary>Typed client for the My Reading (completion/rating) endpoints.</summary>
public sealed class FeedbackApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<WorkFeedbackListResponse> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<WorkFeedbackListResponse>(
            "api/v1/me/feedback",
            cancellationToken);

        return response ?? new WorkFeedbackListResponse([]);
    }

    public async Task<WorkFeedbackResponse?> FindAsync(
        Guid workId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/v1/me/feedback/{workId}", cancellationToken);

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await response.Content.ReadFromJsonAsync<WorkFeedbackResponse>(cancellationToken);
    }

    public async Task<SetFeedbackOutcome> SetAsync(
        Guid workId,
        DateOnly completedOn,
        int rating,
        uint? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"api/v1/me/feedback/{workId}",
            new SetWorkFeedbackRequest(completedOn, rating, expectedVersion),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new SetFeedbackOutcome(
                true,
                await response.Content.ReadFromJsonAsync<WorkFeedbackResponse>(cancellationToken),
                null)
            : new SetFeedbackOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
    }

    public async Task<RemoveFeedbackOutcome> RemoveAsync(
        Guid workId,
        uint expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"api/v1/me/feedback/{workId}",
            new RemoveWorkFeedbackRequest(expectedVersion),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new RemoveFeedbackOutcome(true, null)
            : new RemoveFeedbackOutcome(false, await ReadErrorAsync(response, cancellationToken));
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
            return "That book is no longer available.";
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

public sealed record SetFeedbackOutcome(bool Succeeded, WorkFeedbackResponse? Feedback, string? Error);

public sealed record RemoveFeedbackOutcome(bool Succeeded, string? Error);
