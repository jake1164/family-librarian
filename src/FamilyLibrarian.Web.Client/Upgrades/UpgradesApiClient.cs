using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Upgrades;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Upgrades;

public sealed record UpgradeCallResult(bool Succeeded, string? Error);

/// <summary>Typed client for the administrator-only view of better copies found for delivered books.</summary>
public sealed class UpgradesApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/upgrades";

    public async Task<IReadOnlyList<LibraryUpgradeResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<LibraryUpgradeResponse[]>($"{BasePath}/", cancellationToken) ?? [];

    public async Task<LibraryUpgradeSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<LibraryUpgradeSettingsResponse>($"{BasePath}/settings", cancellationToken)
        ?? throw new InvalidOperationException("The host returned no upgrade settings.");

    public Task<UpgradeCallResult> SetAutoReplaceAsync(bool autoReplace, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/settings", new SetLibraryUpgradeSettingsRequest(autoReplace), cancellationToken);

    public Task<UpgradeCallResult> ApproveAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{BasePath}/{id}/approve", null, cancellationToken);

    public Task<UpgradeCallResult> DismissAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{BasePath}/{id}/dismiss", null, cancellationToken);

    private async Task<UpgradeCallResult> SendAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return new UpgradeCallResult(true, null);
        return new UpgradeCallResult(false, response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Only an administrator can do that.",
            HttpStatusCode.NotFound => "That proposal no longer exists.",
            HttpStatusCode.BadRequest => await ReadReasonAsync(response, cancellationToken),
            _ => "The host could not complete that. Try again."
        });
    }

    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken);
            return string.IsNullOrWhiteSpace(body?.Error) ? "That could not be done." : body.Error;
        }
        catch (System.Text.Json.JsonException)
        {
            return "That could not be done.";
        }
    }

    private sealed record ErrorBody(string? Error);
}

/// <summary>Plain-language wording for the upgrades page. Colors come from <c>MediaTypeVisuals</c>, never from here.</summary>
public static class UpgradeText
{
    /// <summary>Why a delivered copy was thought incomplete, in words. Unknown codes are shown as they are, never hidden.</summary>
    public static string Reason(string code) => code switch
    {
        "artifact.completeness.sample_marker_in_metadata" => "Labelled a sample or excerpt",
        "artifact.completeness.toc_targets_missing" => "Contents entries point at missing text",
        "artifact.completeness.spine_content_missing" => "Reading-order content is missing",
        "artifact.completeness.duplicate_reading_order_items" => "Repeated sections",
        "artifact.audio.track_set_gap" => "Tracks missing from the set",
        "artifact.audio.track_set_duplicate" => "Duplicate tracks in the set",
        _ => code
    };

    /// <summary>The status chip's label; a failed upgrade is "stopped" here (the shared "Failed" label reads "Scan failed").</summary>
    public static string? StatusLabel(string state) => state == "Failed" ? "Upgrade stopped" : null;

    public static string WhatHappensNext(string state) => state switch
    {
        "Proposed" => "Nothing has changed. Start it to fetch the better copy, or set it aside.",
        "Acquiring" => "The better copy is being fetched and checked. The current copy is untouched.",
        "Publishing" => "The better copy is in the library next to the current one, waiting to be confirmed.",
        "Verified" => "The better copy is confirmed. The incomplete one is being removed.",
        "Completed" => "The better copy replaced the incomplete one.",
        "Dismissed" => "Set aside. This candidate will not be proposed again.",
        "Failed" => "Stopped. Both copies were left as they were; see the reason.",
        _ => string.Empty
    };

    public static bool CanAct(string state) => state == "Proposed";
}
