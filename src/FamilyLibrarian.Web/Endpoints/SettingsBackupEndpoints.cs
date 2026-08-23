using FamilyLibrarian.Contracts.SettingsBackups;
using FamilyLibrarian.Infrastructure.SettingsBackups;
using Microsoft.AspNetCore.Mvc;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Administrative settings-only archive endpoints. Full database recovery stays
/// in the operator-run PostgreSQL procedure documented for this application.
/// </summary>
internal static class SettingsBackupEndpoints
{
    private const long MaxArchiveBytes = 1_048_576;

    public static void MapSettingsBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var backups = app.MapGroup("/api/v1/admin/settings-backup")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        backups.MapPost("/", ExportAsync);
        backups.MapPost("/preview", PreviewAsync);
        backups.MapPost("/import", ImportAsync);
    }

    private static async Task<IResult> ExportAsync(
        CreateSettingsBackupRequest request,
        SettingsBackupService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var archive = await service.ExportAsync(request.Passphrase, cancellationToken);
            return Results.File(
                archive,
                "application/octet-stream",
                $"family-librarian-settings-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{SettingsBackupService.FileExtension}");
        }
        catch (SettingsBackupException exception)
        {
            return Invalid("passphrase", exception.Message);
        }
    }

    private static async Task<IResult> PreviewAsync(
        IFormFile archive,
        [FromForm] string passphrase,
        SettingsBackupService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var preview = await service.PreviewAsync(
                await ReadArchiveAsync(archive, cancellationToken), passphrase, cancellationToken);
            return Results.Ok(new SettingsBackupPreviewResponse(
                preview.CreatedUtc,
                preview.AppVersion,
                preview.SchemaVersion,
                ToCounts(preview.Counts),
                preview.CanImport,
                preview.ExistingSections));
        }
        catch (SettingsBackupException exception)
        {
            return Invalid("archive", exception.Message);
        }
    }

    private static async Task<IResult> ImportAsync(
        IFormFile archive,
        [FromForm] string passphrase,
        SettingsBackupService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var imported = await service.ImportAsync(
                await ReadArchiveAsync(archive, cancellationToken), passphrase, cancellationToken);
            return Results.Ok(new SettingsBackupImportResponse(imported.BackupId, ToCounts(imported.Counts)));
        }
        catch (SettingsBackupException exception)
        {
            return Invalid("archive", exception.Message);
        }
    }

    private static async Task<byte[]> ReadArchiveAsync(IFormFile archive, CancellationToken cancellationToken)
    {
        if (archive is null || archive.Length <= 0 || archive.Length > MaxArchiveBytes)
        {
            throw new SettingsBackupException("Choose a settings archive no larger than 1 MiB.");
        }

        await using var stream = archive.OpenReadStream();
        using var buffer = new MemoryStream((int)archive.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static SettingsBackupCountsResponse ToCounts(SettingsBackupCounts counts) => new(
        counts.CwaSettings,
        counts.AudiobookshelfSettings,
        counts.PrivateEgressGatewaySettings,
        counts.ProviderSettings,
        counts.OidcSettings,
        counts.AcquisitionPolicySettings);
}
