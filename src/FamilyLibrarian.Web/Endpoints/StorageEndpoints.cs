using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Contracts.Storage;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>Administrator-only view of what the installation is holding and what uses the disk. Read-only; the service checks for an administrator itself.</summary>
internal static class StorageEndpoints
{
    public static void MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/storage").RequireAuthorization("Admin");
        group.MapGet("/", GetAsync);
    }

    private static async Task<IResult> GetAsync(StorageInsightService service, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(cancellationToken);
        if (result.Forbidden) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var insight = result.Insight!;
        return Results.Ok(new StorageInsightResponse(
            insight.AsOfUtc, new StorageVolumeResponse(insight.Volume.FreeBytes, insight.Volume.TotalBytes),
            [.. insight.Zones.Select(zone => new StorageZoneResponse(
                zone.Zone.ToString(), zone.Files, zone.Bytes, zone.OldestFileUtc, zone.UnrecognizedFiles, zone.UnrecognizedBytes))],
            [.. insight.HeldFiles.Select(group => new HeldFileGroupResponse(
                group.State.ToString(), group.MediaType.ToString(), group.RuledOut, group.Files, group.Bytes, group.OldestUpdatedAtUtc))],
            [.. insight.Tables.Select(table => new StorageTableResponse(table.Schema, table.Table, table.ApproximateRows, table.ApproximateBytes))],
            insight.DatabaseApproximateBytes));
    }
}
