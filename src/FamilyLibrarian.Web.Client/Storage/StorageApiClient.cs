using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Storage;

namespace FamilyLibrarian.Web.Client.Storage;

/// <summary>Typed client for the administrator-only storage view.</summary>
public sealed class StorageApiClient(HttpClient httpClient)
{
    public async Task<StorageInsightResponse> GetAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<StorageInsightResponse>("api/v1/admin/storage/", cancellationToken)
        ?? throw new InvalidOperationException("The host returned no storage information.");
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

    public static string? FreeSpaceNote(StorageVolumeResponse volume) => volume switch
    {
        { FreeBytes: { } free, TotalBytes: { } total } when total > 0 =>
            $"{Size(free)} free of {Size(total)} ({100.0 * free / total:0}% free)",
        { FreeBytes: { } free } => $"{Size(free)} free",
        _ => null
    };
}
