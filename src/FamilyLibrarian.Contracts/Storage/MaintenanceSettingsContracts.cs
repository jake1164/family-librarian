namespace FamilyLibrarian.Contracts.Storage;

/// <summary>Every clean-up and retention period. Days unless the name says otherwise (hours for the unrecorded-file grace, gigabytes for the held-files limit).</summary>
public sealed record MaintenanceValuesDto(
    int RejectedDays, int RuledOutDays, int WaitingEbookDays, int WaitingAudiobookDays, int StuckDays, int HeldCapGb, int OrphanGraceHours,
    int PressureDays, int EvidenceDays, int SemanticFindingDays, int AttemptDays, int SearchMemoryDays, int NotificationDays, int AlertDays,
    int CommunicationDays, int AuditDays, int UpgradeScanIntervalDays);

public sealed record MaintenanceLimitResponse(int Min, int Max);

public sealed record MaintenanceRunResponse(DateTimeOffset? LastRunAtUtc, int Count, int Failures, DateTimeOffset? NextRunAtUtc);

/// <summary><paramref name="Limits"/> is keyed by the camel-case name of each value, so the screen shows exactly what the host enforces.</summary>
public sealed record MaintenanceSettingsResponse(
    MaintenanceValuesDto Values, MaintenanceValuesDto Defaults, bool IsCustomized, IReadOnlyDictionary<string, MaintenanceLimitResponse> Limits,
    MaintenanceRunResponse Storage, MaintenanceRunResponse Database, MaintenanceRunResponse Upgrades);

public sealed record MaintenanceProblemsResponse(IReadOnlyList<string> Problems);
