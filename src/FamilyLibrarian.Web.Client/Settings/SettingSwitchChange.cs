namespace FamilyLibrarian.Web.Client.Settings;

/// <summary>
/// One requested change from a <c>SettingSwitch</c>. The handler saves it and updates the value the switch is bound to; if the
/// change cannot be made it calls <see cref="Refuse"/> with the reason, which the switch keeps on screen.
/// </summary>
public sealed class SettingSwitchChange(bool value)
{
    /// <summary>The state the person asked for.</summary>
    public bool Value { get; } = value;

    public string? Refusal { get; private set; }

    public void Refuse(string? reason) =>
        Refusal = string.IsNullOrWhiteSpace(reason) ? "That change could not be saved." : reason;
}
