using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Storage;

/// <summary>Every clean-up and retention period an administrator can set, in whole days (hours for the orphan grace, gigabytes for the cap).</summary>
public sealed record MaintenanceSettingsValues(
    int RejectedDays, int RuledOutDays, int WaitingEbookDays, int WaitingAudiobookDays, int StuckDays, int HeldCapGb, int OrphanGraceHours,
    int PressureDays, int EvidenceDays, int SemanticFindingDays, int AttemptDays, int SearchMemoryDays, int NotificationDays, int AlertDays,
    int CommunicationDays, int AuditDays, int UpgradeScanIntervalDays)
{
    public static MaintenanceSettingsValues From(StorageRetentionOptions storage, DatabaseRetentionOptions database, LibraryUpgradeOptions upgrades) => new(
        storage.RejectedDays, storage.RuledOutDays, storage.WaitingEbookDays, storage.WaitingAudiobookDays, storage.StuckDays,
        (int)(storage.HeldBytesCap / StorageRetentionOptions.Gigabyte), storage.OrphanGraceHours, storage.PressureDays, database.EvidenceDays,
        database.SemanticFindingDays, database.AttemptDays, database.SearchMemoryDays, database.NotificationDays, database.AlertDays,
        database.CommunicationDays, database.AuditDays, upgrades.ScanIntervalDays);

    public static MaintenanceSettingsValues From(MaintenanceSettings row) => new(
        row.RejectedDays, row.RuledOutDays, row.WaitingEbookDays, row.WaitingAudiobookDays, row.StuckDays, row.HeldCapGb, row.OrphanGraceHours,
        row.PressureDays, row.EvidenceDays, row.SemanticFindingDays, row.AttemptDays, row.SearchMemoryDays, row.NotificationDays, row.AlertDays,
        row.CommunicationDays, row.AuditDays, row.UpgradeScanIntervalDays);

    /// <summary>Every field that is outside its fixed limits, by label, for a message the administrator can act on.</summary>
    public IReadOnlyList<string> OutOfRange()
    {
        var problems = new List<string>();
        void Check(string label, int value, (int Min, int Max) limit)
        {
            if (value < limit.Min || value > limit.Max) problems.Add($"{label} must be between {limit.Min} and {limit.Max}.");
        }

        Check("Rejected files", RejectedDays, MaintenanceLimits.RejectedDays);
        Check("Copies the search moved past", RuledOutDays, MaintenanceLimits.RuledOutDays);
        Check("Held ebooks waiting for a person", WaitingEbookDays, MaintenanceLimits.WaitingEbookDays);
        Check("Held audiobooks waiting for a person", WaitingAudiobookDays, MaintenanceLimits.WaitingAudiobookDays);
        Check("Files stuck before trust", StuckDays, MaintenanceLimits.StuckDays);
        Check("Held files size limit (GB)", HeldCapGb, MaintenanceLimits.HeldCapGb);
        Check("Unrecorded file grace (hours)", OrphanGraceHours, MaintenanceLimits.OrphanGraceHours);
        Check("Rejected and ruled-out copies when the disk is low", PressureDays, MaintenanceLimits.PressureDays);
        Check("File-check evidence", EvidenceDays, MaintenanceLimits.EvidenceDays);
        Check("AI findings", SemanticFindingDays, MaintenanceLimits.SemanticFindingDays);
        Check("Attempt history", AttemptDays, MaintenanceLimits.AttemptDays);
        Check("Search memory", SearchMemoryDays, MaintenanceLimits.SearchMemoryDays);
        Check("Notifications", NotificationDays, MaintenanceLimits.NotificationDays);
        Check("Closed alerts", AlertDays, MaintenanceLimits.AlertDays);
        Check("Sent messages", CommunicationDays, MaintenanceLimits.CommunicationDays);
        Check("Audit log", AuditDays, MaintenanceLimits.AuditDays);
        Check("Better-copy search interval", UpgradeScanIntervalDays, MaintenanceLimits.UpgradeScanIntervalDays);
        return problems;
    }
}

public sealed record MaintenanceRunInfo(DateTimeOffset? LastRunAtUtc, int Count, int Failures, TimeSpan Interval)
{
    public DateTimeOffset? NextRunAtUtc => LastRunAtUtc + Interval;
}

public sealed record MaintenanceSettingsView(
    MaintenanceSettingsValues Values, MaintenanceSettingsValues Defaults, bool IsCustomized, MaintenanceRunInfo Storage, MaintenanceRunInfo Database,
    MaintenanceRunInfo Upgrades);

public interface IMaintenanceSettingsStore
{
    Task<MaintenanceSettings?> FindAsync(CancellationToken cancellationToken);

    void Add(MaintenanceSettings settings);

    void Remove(MaintenanceSettings settings);

    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed record MaintenanceSettingsResult(bool Succeeded, bool Forbidden, IReadOnlyList<string> Problems)
{
    public static MaintenanceSettingsResult Ok() => new(true, false, []);

    public static MaintenanceSettingsResult NotAllowed() => new(false, true, []);

    public static MaintenanceSettingsResult Invalid(IReadOnlyList<string> problems) => new(false, false, problems);
}

/// <summary>
/// The clean-up and retention settings screen's service, and where the background workers get the periods they run with. A saved row wins
/// over the configured defaults; "reset" removes it. Saving is refused (with every problem listed) when a value is outside its limits,
/// never quietly changed.
/// </summary>
public sealed class MaintenanceSettingsService(
    IMaintenanceSettingsStore store, StorageRetentionOptions storageDefaults, DatabaseRetentionOptions databaseDefaults,
    LibraryUpgradeOptions upgradeDefaults, ICurrentUser currentUser, ICurrentAdministrator administrator, IAuditWriter audit, IClock clock)
{
    public static readonly TimeSpan StorageInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan DatabaseInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan UpgradeInterval = TimeSpan.FromHours(24);

    private MaintenanceSettingsValues Defaults => MaintenanceSettingsValues.From(storageDefaults, databaseDefaults, upgradeDefaults);

    public async Task<(bool Forbidden, MaintenanceSettingsView? View)> GetAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return (true, null);
        var row = await store.FindAsync(cancellationToken);
        return (false, new MaintenanceSettingsView(
            row is { Customized: true } ? MaintenanceSettingsValues.From(row) : Defaults, Defaults, row is { Customized: true },
            new MaintenanceRunInfo(row?.StorageLastRunAtUtc, row?.StorageLastRunRemoved ?? 0, row?.StorageLastRunFailures ?? 0, StorageInterval),
            new MaintenanceRunInfo(row?.DatabaseLastRunAtUtc, row?.DatabaseLastRunRemoved ?? 0, row?.DatabaseLastRunFailures ?? 0, DatabaseInterval),
            new MaintenanceRunInfo(row?.UpgradeLastRunAtUtc, row?.UpgradeLastRunProposed ?? 0, row?.UpgradeLastRunFailures ?? 0, UpgradeInterval)));
    }

    public async Task<MaintenanceSettingsResult> UpdateAsync(MaintenanceSettingsValues values, CancellationToken cancellationToken)
    {
        if (!IsAdmin) return MaintenanceSettingsResult.NotAllowed();
        var problems = values.OutOfRange();
        if (problems.Count > 0) return MaintenanceSettingsResult.Invalid(problems);
        var row = await store.FindAsync(cancellationToken);
        if (row is null)
        {
            row = new MaintenanceSettings(clock.UtcNow);
            store.Add(row);
        }

        row.SetValues(values.RejectedDays, values.RuledOutDays, values.WaitingEbookDays, values.WaitingAudiobookDays, values.StuckDays, values.HeldCapGb,
            values.OrphanGraceHours, values.PressureDays, values.EvidenceDays, values.SemanticFindingDays, values.AttemptDays, values.SearchMemoryDays,
            values.NotificationDays, values.AlertDays, values.CommunicationDays, values.AuditDays, values.UpgradeScanIntervalDays, currentUser.UserId, clock.UtcNow);
        row.SetCustomized(true);
        await store.SaveAsync(cancellationToken);
        await audit.WriteAsync(AuditActions.MaintenanceSettingsChanged, AuditSubjectTypes.Storage, null, new { Values = values, ActorUserId = currentUser.UserId }, cancellationToken);
        return MaintenanceSettingsResult.Ok();
    }

    public async Task<MaintenanceSettingsResult> ResetAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return MaintenanceSettingsResult.NotAllowed();
        var row = await store.FindAsync(cancellationToken);
        if (row is not null)
        {
            // The last-run record lives on the same row; keep it so a reset does not forget when clean-up last ran.
            row.SetValues(Defaults.RejectedDays, Defaults.RuledOutDays, Defaults.WaitingEbookDays, Defaults.WaitingAudiobookDays, Defaults.StuckDays, Defaults.HeldCapGb,
                Defaults.OrphanGraceHours, Defaults.PressureDays, Defaults.EvidenceDays, Defaults.SemanticFindingDays, Defaults.AttemptDays, Defaults.SearchMemoryDays,
                Defaults.NotificationDays, Defaults.AlertDays, Defaults.CommunicationDays, Defaults.AuditDays, Defaults.UpgradeScanIntervalDays, currentUser.UserId, clock.UtcNow);
            row.SetCustomized(false);
            await store.SaveAsync(cancellationToken);
        }

        await audit.WriteAsync(AuditActions.MaintenanceSettingsReset, AuditSubjectTypes.Storage, null, new { ActorUserId = currentUser.UserId }, cancellationToken);
        return MaintenanceSettingsResult.Ok();
    }

    /// <summary>The periods the background workers run with: the saved values when there are any, otherwise the configured defaults.</summary>
    public async Task<(StorageRetentionOptions Storage, DatabaseRetentionOptions Database, LibraryUpgradeOptions Upgrades)> EffectiveAsync(CancellationToken cancellationToken)
    {
        var row = await store.FindAsync(cancellationToken);
        if (row is not { Customized: true }) return (storageDefaults, databaseDefaults, upgradeDefaults);
        return (
            (storageDefaults with
            {
                RejectedDays = row.RejectedDays, RuledOutDays = row.RuledOutDays, WaitingEbookDays = row.WaitingEbookDays, WaitingAudiobookDays = row.WaitingAudiobookDays,
                StuckDays = row.StuckDays, HeldBytesCap = row.HeldCapGb * StorageRetentionOptions.Gigabyte, OrphanGraceHours = row.OrphanGraceHours, PressureDays = row.PressureDays
            }).Clamped(),
            (databaseDefaults with
            {
                EvidenceDays = row.EvidenceDays, SemanticFindingDays = row.SemanticFindingDays, AttemptDays = row.AttemptDays, SearchMemoryDays = row.SearchMemoryDays,
                NotificationDays = row.NotificationDays, AlertDays = row.AlertDays, CommunicationDays = row.CommunicationDays, AuditDays = row.AuditDays
            }).Clamped(),
            (upgradeDefaults with { ScanIntervalDays = row.UpgradeScanIntervalDays }).Clamped());
    }

    public Task RecordStorageRunAsync(int removed, int failures, CancellationToken cancellationToken) =>
        RecordAsync(row => row.RecordStorageRun(clock.UtcNow, removed, failures), cancellationToken);

    public Task RecordDatabaseRunAsync(int removed, int failures, CancellationToken cancellationToken) =>
        RecordAsync(row => row.RecordDatabaseRun(clock.UtcNow, removed, failures), cancellationToken);

    public Task RecordUpgradeRunAsync(int proposed, int failures, CancellationToken cancellationToken) =>
        RecordAsync(row => row.RecordUpgradeRun(clock.UtcNow, proposed, failures), cancellationToken);

    private async Task RecordAsync(Action<MaintenanceSettings> record, CancellationToken cancellationToken)
    {
        var row = await store.FindAsync(cancellationToken);
        if (row is null)
        {
            // A first run with nothing customised still records itself, at the configured defaults, so the screen can say when it last ran.
            row = new MaintenanceSettings(clock.UtcNow);
            var d = Defaults;
            row.SetValues(d.RejectedDays, d.RuledOutDays, d.WaitingEbookDays, d.WaitingAudiobookDays, d.StuckDays, d.HeldCapGb, d.OrphanGraceHours, d.PressureDays,
                d.EvidenceDays, d.SemanticFindingDays, d.AttemptDays, d.SearchMemoryDays, d.NotificationDays, d.AlertDays, d.CommunicationDays, d.AuditDays,
                d.UpgradeScanIntervalDays, null, clock.UtcNow);
            store.Add(row);
        }

        record(row);
        await store.SaveAsync(cancellationToken);
    }

    private bool IsAdmin => currentUser.UserId is not null && administrator.IsAdministrator;
}

/// <summary>
/// One background pass of each kind, exactly as the hosted workers run them: read the administrator's saved periods, run with them, and
/// remember when it ran. Kept apart from the timers so the wiring is testable.
/// </summary>
public sealed class MaintenanceCycle(
    MaintenanceSettingsService settings, StorageMaintenanceService storage, DatabaseRetentionService database, LibraryUpgradeScanService upgrades)
{
    public async Task<StorageMaintenanceReport> RunStorageAsync(CancellationToken cancellationToken)
    {
        var (effective, _, _) = await settings.EffectiveAsync(cancellationToken);
        var report = await storage.RunAsync(effective, cancellationToken);
        await settings.RecordStorageRunAsync(report.TotalRemoved, report.Failures, cancellationToken);
        return report;
    }

    public async Task<DatabaseRetentionReport> RunDatabaseAsync(CancellationToken cancellationToken)
    {
        var (_, effective, _) = await settings.EffectiveAsync(cancellationToken);
        var report = await database.RunAsync(effective, cancellationToken);
        await settings.RecordDatabaseRunAsync(report.TotalRemoved, report.Failures, cancellationToken);
        return report;
    }

    public async Task<LibraryUpgradeScanReport> RunUpgradeScanAsync(CancellationToken cancellationToken)
    {
        var (_, _, effective) = await settings.EffectiveAsync(cancellationToken);
        var report = await upgrades.RunAsync(effective, cancellationToken);
        await settings.RecordUpgradeRunAsync(report.Proposed, report.Failures, cancellationToken);
        return report;
    }
}
