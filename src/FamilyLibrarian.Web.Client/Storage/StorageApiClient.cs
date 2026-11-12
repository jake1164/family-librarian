using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Storage;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Storage;

/// <summary>The outcome of saving or resetting the clean-up settings. <paramref name="Problems"/> lists every value that was refused and why.</summary>
public sealed record MaintenanceSaveResult(MaintenanceSettingsResponse? Settings, IReadOnlyList<string> Problems, string? Error)
{
    public bool Succeeded => Settings is not null;
}

/// <summary>Typed client for the administrator-only storage view and its clean-up settings.</summary>
public sealed class StorageApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string SettingsPath = "api/v1/admin/storage/settings";

    public async Task<StorageInsightResponse> GetAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<StorageInsightResponse>("api/v1/admin/storage/", cancellationToken)
        ?? throw new InvalidOperationException("The host returned no storage information.");

    public async Task<MaintenanceSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await httpClient.GetFromJsonAsync<MaintenanceSettingsResponse>(SettingsPath, cancellationToken);
        // A reply that is not the settings (an empty or different document) is a failure to load, never a screen of zeros.
        return settings is { Values: not null, Defaults: not null, Limits: not null, Storage: not null, Database: not null, Upgrades: not null }
            ? settings
            : throw new InvalidOperationException("The host returned no clean-up settings.");
    }

    public Task<MaintenanceSaveResult> SaveSettingsAsync(MaintenanceValuesDto values, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, values, cancellationToken);

    public Task<MaintenanceSaveResult> ResetSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, null, cancellationToken);

    private async Task<MaintenanceSaveResult> SendAsync(HttpMethod method, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, SettingsPath);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return new MaintenanceSaveResult(await response.Content.ReadFromJsonAsync<MaintenanceSettingsResponse>(cancellationToken), [], null);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problems = await response.Content.ReadFromJsonAsync<MaintenanceProblemsResponse>(cancellationToken);
            return new MaintenanceSaveResult(null, problems?.Problems ?? [], null);
        }

        return new MaintenanceSaveResult(null, [], response.StatusCode == HttpStatusCode.Forbidden
            ? "Only an administrator can change these settings."
            : "The host could not save the settings. Try again.");
    }
}

/// <summary>Plain-language formatting for the storage page.</summary>
public static class StorageText
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} MB",
        _ => $"{(bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)} GB"
    };

    /// <summary>How long ago, in whole days (or hours for under a day).</summary>
    public static string Age(DateTimeOffset? when, DateTimeOffset now) => when is not { } at ? "—" : (now - at) switch
    {
        { TotalHours: < 1 } => "under an hour",
        { TotalDays: < 1 } span => $"{(int)span.TotalHours} h",
        var span => $"{(int)span.TotalDays} d"
    };

    public static string Zone(string zone) => zone switch
    {
        "Quarantine" => "Received, awaiting scan",
        "Processing" => "Being checked",
        "Trusted" => "Passed, awaiting delivery",
        "Unmatched" => "Held for identification",
        "Rejected" => "Failed a security check",
        _ => zone
    };

    /// <summary>When a clean-up last ran and what it did, or that it has not run yet.</summary>
    public static string RunSummary(MaintenanceRunResponse run, DateTimeOffset now, string unit, bool next = true)
    {
        if (run.LastRunAtUtc is not { } last) return "It has not run yet. The first pass runs shortly after the host starts.";
        var outcome = run.Failures > 0
            ? $"{run.Count} {unit}, with {run.Failures} step{(run.Failures == 1 ? "" : "s")} that failed and will be retried"
            : $"{run.Count} {unit}";
        var when = next && run.NextRunAtUtc is { } due ? $" Next pass {(due <= now ? "is due now" : "in about " + Until(due - now))}." : string.Empty;
        return $"Last ran {Age(last, now)} ago: {outcome}.{when}";
    }

    private static string Until(TimeSpan span) => span switch
    {
        { TotalHours: < 1 } => "under an hour",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalDays} d"
    };

    /// <summary>A removal date as a plain calendar date, or a dash when nothing is scheduled.</summary>
    public static string Date(DateTimeOffset? date) =>
        date is { } at ? at.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture) : "—";

    public static string? FreeSpaceNote(StorageVolumeResponse volume) => volume switch
    {
        { FreeBytes: { } free, TotalBytes: { } total } when total > 0 =>
            $"{Size(free)} free of {Size(total)} ({100.0 * free / total:0}% free)",
        { FreeBytes: { } free } => $"{Size(free)} free",
        _ => null
    };
}
