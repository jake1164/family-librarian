using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Requests;

/// <summary>Typed client for the request workflow endpoints.</summary>
public sealed class RequestsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<BookRequestListResponse> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<BookRequestListResponse>(
            "api/v1/me/requests",
            cancellationToken);

        return response ?? new BookRequestListResponse([], []);
    }

    public async Task<CreateRequestOutcome> CreateAsync(
        Guid workId,
        IReadOnlyList<string> formats,
        string? note,
        bool confirmDuplicate,
        bool confirmOwned,
        string? versionKind = null,
        string? versionDetails = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/requests/",
            new CreateBookRequestRequest(workId, formats, note, confirmDuplicate, confirmOwned, versionKind, versionDetails),
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return CreateRequestOutcome.Created(
                await response.Content.ReadFromJsonAsync<BookRequestResponse>(cancellationToken));
        }

        // The host answers an overlapping-request or already-owned format with
        // 409 and a discriminated payload, so the user can decide rather than
        // being blocked.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<CreateBookRequestConflictResponse>(cancellationToken);
            return conflict?.Kind == "Owned"
                ? CreateRequestOutcome.AlreadyOwned(conflict.Owned)
                : CreateRequestOutcome.Duplicate(conflict?.Duplicate);
        }

        return CreateRequestOutcome.Failed(await ReadErrorAsync(response, cancellationToken));
    }

    public async Task<ChangeStatusOutcome> ChangeStatusAsync(
        Guid requestId,
        string status,
        string? reason = null,
        uint? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/requests/{requestId}/transitions",
            new ChangeBookRequestStatusRequest(status, reason, expectedVersion),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new ChangeStatusOutcome(
                true,
                await response.Content.ReadFromJsonAsync<BookRequestResponse>(cancellationToken),
                null)
            : new ChangeStatusOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
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
            return "That book or request is no longer available.";
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

public sealed record CreateRequestOutcome(
    CreateRequestStatus Status,
    BookRequestResponse? Request,
    BookRequestDuplicateResponse? Existing,
    BookRequestOwnedResponse? Owned,
    string? Error)
{
    public static CreateRequestOutcome Created(BookRequestResponse? request) =>
        new(CreateRequestStatus.Created, request, null, null, null);

    public static CreateRequestOutcome Duplicate(BookRequestDuplicateResponse? existing) =>
        new(CreateRequestStatus.DuplicateWarning, null, existing, null, null);

    public static CreateRequestOutcome AlreadyOwned(BookRequestOwnedResponse? owned) =>
        new(CreateRequestStatus.OwnedWarning, null, null, owned, null);

    public static CreateRequestOutcome Failed(string error) =>
        new(CreateRequestStatus.Failed, null, null, null, error);
}

public enum CreateRequestStatus
{
    Created,
    DuplicateWarning,
    OwnedWarning,
    Failed
}

public sealed record ChangeStatusOutcome(
    bool Succeeded,
    BookRequestResponse? Request,
    string? Error);
