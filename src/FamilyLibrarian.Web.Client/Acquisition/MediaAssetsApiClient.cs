using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Security;
using FamilyLibrarian.Web.Client.Authentication;
using Microsoft.AspNetCore.Components.Forms;

namespace FamilyLibrarian.Web.Client.Acquisition;

/// <summary>
/// Typed client for the manual-import upload and the acquisition/security
/// review queue.
/// </summary>
public sealed class MediaAssetsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    /// <summary>The server enforces its own cap; this only avoids buffering an
    /// obviously oversized file into memory before the request is even sent.</summary>
    private const long MaxUploadSizeBytes = 500L * 1024 * 1024;

    public async Task<IReadOnlyList<MediaAssetAdminResponse>> GetQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<MediaAssetAdminListResponse>(
            "api/v1/admin/media-assets/", cancellationToken);
        return response?.Assets ?? [];
    }

    public async Task<ManualImportOutcome> ImportAsync(
        Guid requestId,
        Guid formatId,
        IBrowserFile file,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import");
        await antiforgery.AttachAsync(request, cancellationToken);

        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(file.OpenReadStream(MaxUploadSizeBytes, cancellationToken));
        content.Add(fileContent, "file", file.Name);
        request.Content = content;

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>(cancellationToken);
            return new ManualImportOutcome(true, result, null);
        }

        return new ManualImportOutcome(false, null, await ReadImportErrorAsync(response, cancellationToken));
    }

    public async Task<ManualImportOutcome> AcquireDirectAsync(
        Guid requestId,
        Guid formatId,
        string providerId,
        string providerResultId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/admin/requests/{requestId}/formats/{formatId}/direct-acquisitions/" +
            $"{Uri.EscapeDataString(providerId)}/{Uri.EscapeDataString(providerResultId)}");
        await antiforgery.AttachAsync(request, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<ManualImportResultResponse>(cancellationToken);
            return new ManualImportOutcome(true, result, null);
        }

        return new ManualImportOutcome(false, null, await ReadImportErrorAsync(response, cancellationToken));
    }

    public Task<MediaAssetActionOutcome> EvaluateAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SendActionAsync($"api/v1/admin/media-assets/{assetId}/evaluate", reason: null, cancellationToken);

    public Task<MediaAssetActionOutcome> ApproveAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SendActionAsync($"api/v1/admin/media-assets/{assetId}/approve", reason: null, cancellationToken);

    public Task<MediaAssetActionOutcome> RejectAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SendActionAsync($"api/v1/admin/media-assets/{assetId}/reject", reason: null, cancellationToken);

    public Task<MediaAssetActionOutcome> DiscardAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SendActionAsync($"api/v1/admin/media-assets/{assetId}", HttpMethod.Delete, reason: null, cancellationToken);

    private async Task<MediaAssetActionOutcome> SendActionAsync(
        string path,
        string? reason,
        CancellationToken cancellationToken)
        => await SendActionAsync(path, HttpMethod.Post, reason, cancellationToken);

    private async Task<MediaAssetActionOutcome> SendActionAsync(
        string path,
        HttpMethod method,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Delete)
        {
            request.Content = JsonContent.Create(new ApprovalDecisionRequest(reason));
        }
        await antiforgery.AttachAsync(request, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new MediaAssetActionOutcome(true, null);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new MediaAssetActionOutcome(false, "That file no longer exists.");
        }

        var problem = await TryReadValidationProblemAsync(response, cancellationToken);
        return new MediaAssetActionOutcome(
            false, problem ?? "That action could not be completed. Please try again.");
    }

    private static async Task<string> ReadImportErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await TryReadAsync<ConflictPayload>(response, cancellationToken);
            return conflict?.Message ?? "A file already exists for this format.";
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var problem = await TryReadAsync<ProblemPayload>(response, cancellationToken);
            return problem?.Detail
                ?? "The security scanner is unavailable, so no file can be accepted right now.";
        }

        var validation = await TryReadValidationProblemAsync(response, cancellationToken);
        return validation ?? "That file could not be imported. Please try again.";
    }

    private static async Task<string?> TryReadValidationProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await TryReadAsync<ValidationProblemPayload>(response, cancellationToken);
        return payload?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault();
    }

    private static async Task<TPayload?> TryReadAsync<TPayload>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<TPayload>(cancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);

    private sealed record ConflictPayload(string? Message);

    private sealed record ProblemPayload(string? Detail);
}

public sealed record ManualImportOutcome(bool Succeeded, ManualImportResultResponse? Result, string? Error);

public sealed record MediaAssetActionOutcome(bool Succeeded, string? Error);
