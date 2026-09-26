using FamilyLibrarian.Contracts.Requests;

namespace FamilyLibrarian.Web.Client.Requests;

/// <summary>Consistent byte and average-rate labels for admin acquisition activity.</summary>
public static class AdminAcquisitionProgressText
{
    public static string Describe(AdminActiveAcquisitionResponse activity)
    {
        var transfer = activity.TotalBytes is { } total
            ? $"{FormatBytes(activity.BytesReceived)} / {FormatBytes(total)}"
            : $"{FormatBytes(activity.BytesReceived)} received";
        if (activity.AverageBytesPerSecond is { } rate)
            transfer += $" · {FormatRate(rate)} avg";
        return $"{activity.Stage} via {activity.ProviderDisplayName} · {transfer}";
    }

    public static string FooterLabel(AdminActiveAcquisitionResponse activity) =>
        $"{activity.Stage}: {activity.WorkTitle ?? "request"} · {FormatBytes(activity.BytesReceived)}"
        + (activity.TotalBytes is { } total ? $" / {FormatBytes(total)}" : " received")
        + (activity.AverageBytesPerSecond is { } rate ? $" · {FormatRate(rate)} avg" : string.Empty);

    private static string FormatRate(long bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
