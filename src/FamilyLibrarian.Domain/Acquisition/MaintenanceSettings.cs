namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// The administrator's clean-up and retention periods, plus when each background clean-up last ran. One row, created the first time
/// they are saved; until then (and after "reset") the configured defaults apply. Values are stored exactly as saved: the application
/// validates them against fixed limits before they get here and clamps again before use.
/// </summary>
public sealed class MaintenanceSettings
{
    private MaintenanceSettings()
    {
    }

    public MaintenanceSettings(DateTimeOffset createdAtUtc)
    {
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public int RejectedDays { get; private set; }

    public int RuledOutDays { get; private set; }

    public int WaitingEbookDays { get; private set; }

    public int WaitingAudiobookDays { get; private set; }

    public int StuckDays { get; private set; }

    public int HeldCapGb { get; private set; }

    public int OrphanGraceHours { get; private set; }

    public int PressureDays { get; private set; }

    public int EvidenceDays { get; private set; }

    public int SemanticFindingDays { get; private set; }

    public int AttemptDays { get; private set; }

    public int SearchMemoryDays { get; private set; }

    public int NotificationDays { get; private set; }

    public int AlertDays { get; private set; }

    public int CommunicationDays { get; private set; }

    public int AuditDays { get; private set; }

    public int UpgradeScanIntervalDays { get; private set; }

    public DateTimeOffset? StorageLastRunAtUtc { get; private set; }

    public int StorageLastRunRemoved { get; private set; }

    public int StorageLastRunFailures { get; private set; }

    public DateTimeOffset? DatabaseLastRunAtUtc { get; private set; }

    public int DatabaseLastRunRemoved { get; private set; }

    public int DatabaseLastRunFailures { get; private set; }

    public DateTimeOffset? UpgradeLastRunAtUtc { get; private set; }

    public int UpgradeLastRunProposed { get; private set; }

    public int UpgradeLastRunFailures { get; private set; }

    /// <summary>
    /// True once an administrator saved values. While false the stored values are ignored and the configured defaults apply, so a row that
    /// exists only to remember when clean-up last ran never freezes the defaults it was created with.
    /// </summary>
    public bool Customized { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void SetValues(
        int rejectedDays, int ruledOutDays, int waitingEbookDays, int waitingAudiobookDays, int stuckDays, int heldCapGb, int orphanGraceHours,
        int pressureDays, int evidenceDays, int semanticFindingDays, int attemptDays, int searchMemoryDays, int notificationDays, int alertDays,
        int communicationDays, int auditDays, int upgradeScanIntervalDays, Guid? actorUserId, DateTimeOffset atUtc)
    {
        RejectedDays = rejectedDays;
        RuledOutDays = ruledOutDays;
        WaitingEbookDays = waitingEbookDays;
        WaitingAudiobookDays = waitingAudiobookDays;
        StuckDays = stuckDays;
        HeldCapGb = heldCapGb;
        OrphanGraceHours = orphanGraceHours;
        PressureDays = pressureDays;
        EvidenceDays = evidenceDays;
        SemanticFindingDays = semanticFindingDays;
        AttemptDays = attemptDays;
        SearchMemoryDays = searchMemoryDays;
        NotificationDays = notificationDays;
        AlertDays = alertDays;
        CommunicationDays = communicationDays;
        AuditDays = auditDays;
        UpgradeScanIntervalDays = upgradeScanIntervalDays;
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = atUtc;
    }

    public void SetCustomized(bool customized) => Customized = customized;

    public void RecordStorageRun(DateTimeOffset atUtc, int removed, int failures)
    {
        StorageLastRunAtUtc = atUtc;
        StorageLastRunRemoved = removed;
        StorageLastRunFailures = failures;
    }

    public void RecordDatabaseRun(DateTimeOffset atUtc, int removed, int failures)
    {
        DatabaseLastRunAtUtc = atUtc;
        DatabaseLastRunRemoved = removed;
        DatabaseLastRunFailures = failures;
    }

    public void RecordUpgradeRun(DateTimeOffset atUtc, int proposed, int failures)
    {
        UpgradeLastRunAtUtc = atUtc;
        UpgradeLastRunProposed = proposed;
        UpgradeLastRunFailures = failures;
    }
}
