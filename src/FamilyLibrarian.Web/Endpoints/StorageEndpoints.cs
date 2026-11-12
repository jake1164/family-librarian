using System.Reflection;
using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Contracts.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Administrator-only view of what the installation is holding, and the clean-up settings. The view is read-only; the service checks for an administrator itself.</summary>
internal static class StorageEndpoints
{
    public static void MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/storage").RequireAuthorization("Admin").AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapGet("/", GetAsync);
        group.MapGet("/settings", GetSettingsAsync);
        group.MapPut("/settings", SaveSettingsAsync);
        group.MapDelete("/settings", ResetSettingsAsync);
    }

    private static async Task<IResult> GetAsync(StorageInsightService service, MaintenanceSettingsService settings, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(cancellationToken);
        if (result.Forbidden) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var insight = result.Insight!;
        var (storage, _, _) = await settings.EffectiveAsync(cancellationToken);
        return Results.Ok(new StorageInsightResponse(
            insight.AsOfUtc, new StorageVolumeResponse(insight.Volume.FreeBytes, insight.Volume.TotalBytes),
            [.. insight.Zones.Select(zone => new StorageZoneResponse(
                zone.Zone.ToString(), zone.Files, zone.Bytes, zone.OldestFileUtc, zone.UnrecognizedFiles, zone.UnrecognizedBytes))],
            [.. insight.HeldFiles.Select(group => new HeldFileGroupResponse(
                group.State.ToString(), group.MediaType.ToString(), group.RuledOut, group.Files, group.Bytes, group.OldestUpdatedAtUtc,
                group.OldestUpdatedAtUtc is { } oldest
                    ? StorageRetentionPlanner.RemovalDate(group.State, group.MediaType, group.RuledOut, group.AwaitingDecision, oldest, storage)
                    : null,
                group.AwaitingDecision))],
            [.. insight.Tables.Select(table => new StorageTableResponse(table.Schema, table.Table, table.ApproximateRows, table.ApproximateBytes))],
            insight.DatabaseApproximateBytes));
    }

    private static async Task<IResult> GetSettingsAsync(MaintenanceSettingsService service, CancellationToken cancellationToken)
    {
        var (forbidden, view) = await service.GetAsync(cancellationToken);
        return forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(ToResponse(view!));
    }

    private static async Task<IResult> SaveSettingsAsync(MaintenanceValuesDto request, MaintenanceSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.UpdateAsync(ToValues(request), cancellationToken);
        if (result.Forbidden) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!result.Succeeded) return Results.BadRequest(new MaintenanceProblemsResponse(result.Problems));
        var (_, view) = await service.GetAsync(cancellationToken);
        return Results.Ok(ToResponse(view!));
    }

    private static async Task<IResult> ResetSettingsAsync(MaintenanceSettingsService service, CancellationToken cancellationToken)
    {
        var result = await service.ResetAsync(cancellationToken);
        if (result.Forbidden) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var (_, view) = await service.GetAsync(cancellationToken);
        return Results.Ok(ToResponse(view!));
    }

    private static MaintenanceSettingsValues ToValues(MaintenanceValuesDto v) => new(
        v.RejectedDays, v.RuledOutDays, v.WaitingEbookDays, v.WaitingAudiobookDays, v.StuckDays, v.HeldCapGb, v.OrphanGraceHours, v.PressureDays, v.EvidenceDays,
        v.SemanticFindingDays, v.AttemptDays, v.SearchMemoryDays, v.NotificationDays, v.AlertDays, v.CommunicationDays, v.AuditDays, v.UpgradeScanIntervalDays);

    private static MaintenanceValuesDto ToDto(MaintenanceSettingsValues v) => new(
        v.RejectedDays, v.RuledOutDays, v.WaitingEbookDays, v.WaitingAudiobookDays, v.StuckDays, v.HeldCapGb, v.OrphanGraceHours, v.PressureDays, v.EvidenceDays,
        v.SemanticFindingDays, v.AttemptDays, v.SearchMemoryDays, v.NotificationDays, v.AlertDays, v.CommunicationDays, v.AuditDays, v.UpgradeScanIntervalDays);

    private static MaintenanceRunResponse ToRun(MaintenanceRunInfo run) => new(run.LastRunAtUtc, run.Count, run.Failures, run.NextRunAtUtc);

    /// <summary>The limits come from the same table the host enforces, keyed by each value's camel-case name.</summary>
    private static Dictionary<string, MaintenanceLimitResponse> Limits() =>
        typeof(MaintenanceLimits).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof((int, int)))
            .ToDictionary(field => char.ToLowerInvariant(field.Name[0]) + field.Name[1..],
                field => { var (min, max) = ((int, int))field.GetValue(null)!; return new MaintenanceLimitResponse(min, max); });

    private static MaintenanceSettingsResponse ToResponse(MaintenanceSettingsView view) => new(
        ToDto(view.Values), ToDto(view.Defaults), view.IsCustomized, Limits(), ToRun(view.Storage), ToRun(view.Database), ToRun(view.Upgrades));

    /// <summary>The date the age rule will remove a held file, for the security queue; null for a file clean-up never touches.</summary>
    internal static DateTimeOffset? RemovalDate(
        MediaAssetStorageState state, RequestMediaType type, DateTimeOffset? ruledOutAtUtc, bool awaitingDecision, DateTimeOffset updatedAtUtc,
        StorageRetentionOptions options) =>
        StorageRetentionPlanner.RemovalDate(state, type, ruledOutAtUtc is not null, awaitingDecision, updatedAtUtc, options);
}
