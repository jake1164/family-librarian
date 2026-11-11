namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// The administrator's choice about automatic replacement of incomplete delivered books. One row, created the first time it is
/// changed; until then the configured default (on) applies. Same singleton-row shape as the other settings.
/// </summary>
public sealed class LibraryUpgradeSettings
{
    private LibraryUpgradeSettings()
    {
    }

    public LibraryUpgradeSettings(DateTimeOffset createdAtUtc)
    {
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>On: a proposal is carried out without waiting for a person when the destination can be changed safely. Off: every proposal waits.</summary>
    public bool AutoReplace { get; private set; } = true;

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void SetAutoReplace(bool autoReplace, Guid? actorUserId, DateTimeOffset atUtc)
    {
        AutoReplace = autoReplace;
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = atUtc;
    }
}
