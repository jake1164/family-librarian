using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Requests;

/// <summary>Typed client for the administrator's request-review queue.</summary>
public sealed class AdminRequestsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<IReadOnlyList<AdminActiveAcquisitionResponse>> GetActiveAcquisitionsAsync(
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<AdminActiveAcquisitionResponse[]>(
            "api/v1/admin/requests/active-acquisitions", cancellationToken) ?? [];

    public async Task<AdminRequestAttentionResponse> GetAttentionAsync(
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<AdminRequestAttentionResponse>(
            "api/v1/admin/requests/attention", cancellationToken)
        ?? new AdminRequestAttentionResponse(0, [], []);

    /// <summary>This request's own waiting provider interaction, or null if none is waiting.</summary>
    public Task<ProviderInteractionResponse?> GetProviderInteractionAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<ProviderInteractionResponse>(
            $"api/v1/admin/requests/{requestId}/provider-interaction", cancellationToken);

    public async Task<IReadOnlyList<AdminBookRequestResponse>> GetQueueAsync(
        string? status,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(status)
            ? "api/v1/admin/requests/"
            : $"api/v1/admin/requests/?status={Uri.EscapeDataString(status)}";
        var response = await httpClient.GetFromJsonAsync<AdminBookRequestListResponse>(path, cancellationToken);
        return response?.Requests ?? [];
    }

    public Task<AdminBookRequestResponse?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<AdminBookRequestResponse>(
            $"api/v1/admin/requests/{requestId}", cancellationToken);

    public async Task<IReadOnlyList<ProviderAttemptResponse>> GetProviderAttemptsAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"api/v1/admin/requests/{requestId}/provider-attempts", cancellationToken) ?? [];

    /// <summary>
    /// ADMIN-DEBUG-1: asks one external provider right now and returns its
    /// raw results, outside that provider's own recheck schedule. Never
    /// acquires anything and writes nothing -- a librarian-facing debug tool.
    /// </summary>
    public async Task<ProviderDebugSearchOutcome> DebugSearchProviderAsync(
        Guid requestId,
        Guid formatId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/admin/requests/{requestId}/formats/{formatId}/providers/{Uri.EscapeDataString(providerId)}/debug-search",
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var candidates = await response.Content.ReadFromJsonAsync<AdminProviderDebugCandidateResponse[]>(
                cancellationToken);
            return new ProviderDebugSearchOutcome(true, candidates ?? [], null);
        }

        return new ProviderDebugSearchOutcome(
            false, [], await ReadDebugSearchErrorAsync(response, cancellationToken));
    }

    private static async Task<string> ReadDebugSearchErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "That request, format, or provider no longer exists.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsPayload>(cancellationToken);
            var message = problem?.Detail ??
                problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // The generic message below is safe when an intermediary returned a
            // response outside the application's problem-details shape.
        }

        return "That provider could not be searched right now. Please try again.";
    }

    private sealed record ProblemDetailsPayload(string? Detail, Dictionary<string, string[]>? Errors);

    public Task<AdminRequestActionOutcome> ChangeStatusAsync(
        Guid requestId,
        string status,
        string? reason,
        uint expectedVersion,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync(
            HttpMethod.Post,
            $"api/v1/admin/requests/{requestId}/transitions",
            new ChangeBookRequestStatusRequest(status, reason, expectedVersion),
            cancellationToken);

    public async Task<RecheckOutcome> RecheckNeedsReviewAsync(
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/requests/recheck")
        {
            Content = JsonContent.Create(new RecheckNeedsReviewRequest(providerId))
        };
        await antiforgery.AttachAsync(request, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<RecheckNeedsReviewResponse>(cancellationToken);
            return new RecheckOutcome(true, body?.RequeuedCount ?? 0, null);
        }

        return new RecheckOutcome(false, 0, await ReadErrorAsync(response, cancellationToken));
    }

    /// <summary>
    /// Admin counterpart of the requester's own needs-review resolve route --
    /// additive, not exclusive. A non-null <paramref name="candidateId"/>
    /// accepts that candidate ("get it anyway"); null declines every offered
    /// candidate ("keep looking").
    /// </summary>
    public Task<AdminRequestActionOutcome> ResolveNeedsReviewAsync(
        Guid requestId,
        Guid? candidateId,
        uint? expectedVersion,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync(
            HttpMethod.Post,
            $"api/v1/admin/requests/{requestId}/needs-review/resolve",
            new ResolveNeedsReviewRequest(candidateId, expectedVersion),
            cancellationToken);

    public Task<AdminRequestActionOutcome> SetNoteAsync(
        Guid requestId,
        string? note,
        uint expectedVersion,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync(
            HttpMethod.Put,
            $"api/v1/admin/requests/{requestId}/note",
            new SetAdminBookRequestNoteRequest(note, expectedVersion),
            cancellationToken);

    private async Task<AdminRequestActionOutcome> SendForOutcomeAsync<TPayload>(
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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new AdminRequestActionOutcome(
                true,
                await response.Content.ReadFromJsonAsync<AdminBookRequestResponse>(cancellationToken),
                null);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new AdminRequestActionOutcome(
                false,
                null,
                "Someone else updated this request. Reload it before making another change.");
        }

        return new AdminRequestActionOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "That request no longer exists.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            var message = problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // The generic message below is safe when an intermediary returned a
            // response outside the application's problem-details shape.
        }

        return "That request could not be updated. Please try again.";
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record AdminRequestActionOutcome(
    bool Succeeded,
    AdminBookRequestResponse? Request,
    string? Error);

public sealed record RecheckOutcome(
    bool Succeeded,
    int RequeuedCount,
    string? Error);

public sealed record ProviderDebugSearchOutcome(
    bool Succeeded,
    IReadOnlyList<AdminProviderDebugCandidateResponse> Candidates,
    string? Error);
