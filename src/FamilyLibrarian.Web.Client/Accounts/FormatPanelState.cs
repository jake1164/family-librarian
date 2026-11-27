namespace FamilyLibrarian.Web.Client.Accounts;

/// <summary>What <c>FormatPreferencesPanel</c> is doing, for a page that must not move on before it has saved.</summary>
public enum FormatPanelState
{
    /// <summary>Loading or saving.</summary>
    Busy,

    /// <summary>The choices could not be loaded, or the last choice was not saved.</summary>
    Blocked,

    /// <summary>What is on screen is what is saved.</summary>
    Ready
}
