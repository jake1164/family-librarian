using MudBlazor;

namespace FamilyLibrarian.Web.Client.Settings;

/// <summary>How every settings page reports a save or removal, and asks before removing something (docs/07-ui-conventions.md).</summary>
public static class SettingsFeedback
{
    public static void Saved(this ISnackbar snackbar, string message) => snackbar.Add(message, Severity.Success);

    /// <summary>A failure stays until it is dismissed, so it cannot disappear before it has been read.</summary>
    public static void Failed(this ISnackbar snackbar, string? message) =>
        snackbar.Add(
            string.IsNullOrWhiteSpace(message) ? "That change could not be saved." : message,
            Severity.Error,
            options => options.RequireInteraction = true);

    public static async Task<bool> ConfirmRemovalAsync(
        this IDialogService dialogs, string title, string message, string yesText = "Remove") =>
        await dialogs.ShowMessageBoxAsync(title, message, yesText: yesText, cancelText: "Cancel") == true;
}
