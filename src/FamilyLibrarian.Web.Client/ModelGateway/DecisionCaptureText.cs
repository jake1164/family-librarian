using System.Globalization;
using FamilyLibrarian.Contracts.ModelGateway;

namespace FamilyLibrarian.Web.Client.ModelGateway;

/// <summary>Plain-language formatting for the saved-decisions panel. Kept out of the Razor file so it is easy to test.</summary>
public static class DecisionCaptureText
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.CurrentCulture)} MB"
    };

    public static string When(DateTimeOffset? value) => value is { } at ? at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "—";

    public static string Plural(int count, string noun) => count == 1 ? $"{count} {noun}" : $"{count} {noun}s";

    public static string Purged(DecisionCapturePurgeResponse purged) =>
        $"Deleted {Plural(purged.Snapshots, "saved decision")}, {Plural(purged.Calls, "call")} and {Plural(purged.Outcomes, "result")}.";
}

/// <summary>The editable copy of the capture settings. The response records are read-only, so the page edits this instead.</summary>
public sealed class DecisionCaptureForm
{
    public bool DecisionCapture { get; set; }

    public bool ChatCapture { get; set; }

    public int SnapshotsPerRole { get; set; }

    public int UnlabeledSnapshotDays { get; set; }

    public int CallsPerSnapshot { get; set; }

    public int FindingsDays { get; set; }

    public bool LimitLabeled { get; set; }

    public int LabeledDays { get; set; }

    public void Load(DecisionCaptureSettingsResponse value)
    {
        DecisionCapture = value.DecisionCaptureEnabled;
        ChatCapture = value.ChatCaptureEnabled;
        SnapshotsPerRole = value.Limits.SnapshotsPerRole;
        UnlabeledSnapshotDays = value.Limits.UnlabeledSnapshotDays;
        CallsPerSnapshot = value.Limits.CallsPerSnapshot;
        FindingsDays = value.Limits.FindingsDays;
        LimitLabeled = value.Limits.LabeledSnapshotDays is not null;
        LabeledDays = value.Limits.LabeledSnapshotDays ?? value.Ranges.MinLabeledSnapshotDays;
    }

    public DecisionCaptureLimitsDto ToLimits() =>
        new(SnapshotsPerRole, UnlabeledSnapshotDays, LimitLabeled ? LabeledDays : null, CallsPerSnapshot, FindingsDays);

    public SaveDecisionCaptureSettingsRequest ToRequest() => new(DecisionCapture, ChatCapture, ToLimits());

    public bool DiffersFrom(DecisionCaptureSettingsResponse saved) =>
        ToRequest() != new SaveDecisionCaptureSettingsRequest(saved.DecisionCaptureEnabled, saved.ChatCaptureEnabled, saved.Limits);
}
