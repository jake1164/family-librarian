using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Communications;

/// <summary>Typed client for a household member's own Matrix identity link (COMM-1 §C).</summary>
public sealed class MatrixLinkApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/me/communications/matrix";

    public Task<MatrixLinkStatusResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<MatrixLinkStatusResponse>($"{BasePath}/", cancellationToken);

    public Task<MatrixLinkResult> RequestLinkAsync(string matrixUserId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, new RequestMatrixLinkRequest(matrixUserId), cancellationToken);

    public Task<MatrixLinkResult> UnlinkAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, null, cancellationToken);

    private async Task<MatrixLinkResult> SendAsync<TPayload>(
        HttpMethod method, TPayload? payload, CancellationToken cancellationToken)
        where TPayload : class
    {
        using var request = new HttpRequestMessage(method, $"{BasePath}/");
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new MatrixLinkResult(true, null,
                await response.Content.ReadFromJsonAsync<MatrixLinkStatusResponse>(cancellationToken));
        }

        return new MatrixLinkResult(false, await ReadErrorAsync(response, cancellationToken), null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            return problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault()
                ?? "That could not be saved.";
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            return "That could not be saved.";
        }
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record MatrixLinkResult(bool Succeeded, string? Error, MatrixLinkStatusResponse? Status);
