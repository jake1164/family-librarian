using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

public interface IModelGatewaySettingsStore
{
    /// <summary>The settings with their role rows, not tracked for change.</summary>
    Task<ModelGatewaySettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>The tracked settings with their role rows, created (unsaved) when none exist.</summary>
    Task<ModelGatewaySettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Fixed data-protection purpose for the stored gateway API key.</summary>
public static class ModelGatewaySecretPurposes
{
    public const string ApiKey = "model-gateway-api-key";
}

public sealed record ModelRoleView(ModelRole Role, bool IsEnabled, ModelPlacement Placement, string? LocalModel, string? CloudModel);

/// <summary>The settings as an administrator may see them. The API key is never included, only whether one is set.</summary>
public sealed record ModelGatewaySettingsView(
    bool IsEnabled,
    string? EndpointUrl,
    bool HasApiKey,
    DateTimeOffset? ApiKeySetAtUtc,
    bool AllowCloud,
    IReadOnlyList<ModelRoleView> Roles);

public sealed record ModelRoleCommand(ModelRole Role, bool IsEnabled, ModelPlacement Placement, string? LocalModel, string? CloudModel);

public sealed record SaveModelGatewaySettings(string? EndpointUrl, bool AllowCloud, IReadOnlyList<ModelRoleCommand> Roles);

public sealed record ModelGatewaySettingsResult(bool Succeeded, bool Forbidden, string? Error, ModelGatewaySettingsView? Settings)
{
    public static ModelGatewaySettingsResult Success(ModelGatewaySettingsView settings) => new(true, false, null, settings);

    public static ModelGatewaySettingsResult Invalid(string error) => new(false, false, error, null);

    public static ModelGatewaySettingsResult NotAllowed() => new(false, true, "Administrator access is required.", null);
}

/// <summary>
/// Administrator configuration of the production model gateway. Every operation checks the caller is an authenticated
/// administrator itself, not only the route. All capabilities are off until an administrator turns them on.
/// </summary>
public sealed class ModelGatewaySettingsService(
    IModelGatewaySettingsStore store,
    ICredentialProtector protector,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ICurrentAdministrator administrator,
    IClock clock)
{
    private const string SubjectId = "model-gateway";

    public async Task<ModelGatewaySettingsResult> GetAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelGatewaySettingsResult.NotAllowed();
        }

        return ModelGatewaySettingsResult.Success(ToView(await store.FindAsync(cancellationToken)));
    }

    public async Task<ModelGatewaySettingsResult> SaveAsync(SaveModelGatewaySettings command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdministrator())
        {
            return ModelGatewaySettingsResult.NotAllowed();
        }

        if (Validate(command) is { } error)
        {
            return ModelGatewaySettingsResult.Invalid(error);
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.Configure(command.EndpointUrl, command.AllowCloud, currentUser.UserId, clock.UtcNow);
        foreach (var role in command.Roles)
        {
            settings.ConfigureRole(role.Role, role.IsEnabled, role.Placement, role.LocalModel, role.CloudModel, currentUser.UserId, clock.UtcNow);
        }

        if (settings.IsEnabled && settings.EndpointUrl is null)
        {
            // A gateway that can no longer reach anything must not stay "enabled".
            settings.SetEnabled(false, currentUser.UserId, clock.UtcNow);
        }

        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelGatewaySettingsChanged, AuditSubjectTypes.ModelGateway, SubjectId,
            new
            {
                settings.AllowCloud,
                Roles = settings.Roles.Select(role => new { Role = role.Role.ToString(), role.IsEnabled, Placement = role.Placement.ToString() })
            },
            cancellationToken);
        return ModelGatewaySettingsResult.Success(ToView(settings));
    }

    public async Task<ModelGatewaySettingsResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelGatewaySettingsResult.NotAllowed();
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (enabled && settings.EndpointUrl is null)
        {
            return ModelGatewaySettingsResult.Invalid("Set the endpoint URL first.");
        }

        settings.SetEnabled(enabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelGatewaySettingsChanged, AuditSubjectTypes.ModelGateway, SubjectId, new { Enabled = enabled }, cancellationToken);
        return ModelGatewaySettingsResult.Success(ToView(settings));
    }

    public async Task<ModelGatewaySettingsResult> SetApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelGatewaySettingsResult.NotAllowed();
        }

        if (ModelConfigurationRules.ApiKeyError(apiKey) is { } error)
        {
            return ModelGatewaySettingsResult.Invalid(error);
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetApiKey(protector.Protect(ModelGatewaySecretPurposes.ApiKey, apiKey.Trim()), protector.FormatVersion, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelGatewaySecretSet, AuditSubjectTypes.ModelGateway, SubjectId, new { Field = "api_key" }, cancellationToken);
        return ModelGatewaySettingsResult.Success(ToView(settings));
    }

    public async Task<ModelGatewaySettingsResult> ClearApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelGatewaySettingsResult.NotAllowed();
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearApiKey(currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelGatewaySecretCleared, AuditSubjectTypes.ModelGateway, SubjectId, new { Field = "api_key" }, cancellationToken);
        return ModelGatewaySettingsResult.Success(ToView(settings));
    }

    internal static ModelGatewaySettingsView ToView(ModelGatewaySettings? settings) => new(
        settings?.IsEnabled ?? false,
        settings?.EndpointUrl,
        settings?.HasApiKey ?? false,
        settings?.ApiKeySetAtUtc,
        settings?.AllowCloud ?? false,
        [.. Enum.GetValues<ModelRole>().Select(role =>
        {
            var setting = settings?.FindRole(role);
            return setting is null
                ? new ModelRoleView(role, false, ModelPlacement.Local, null, null)
                : new ModelRoleView(role, setting.IsEnabled, setting.Placement, setting.LocalModel, setting.CloudModel);
        })]);

    private bool IsAdministrator() => currentUser.UserId is not null && administrator.IsAdministrator;

    private static string? Validate(SaveModelGatewaySettings command)
    {
        if (ModelConfigurationRules.EndpointError(command.EndpointUrl) is { } endpointError)
        {
            return endpointError;
        }

        var seen = new HashSet<ModelRole>();
        foreach (var role in command.Roles)
        {
            if (!Enum.IsDefined(role.Role) || !Enum.IsDefined(role.Placement) || !seen.Add(role.Role))
            {
                return "A model role was missing, unknown or repeated.";
            }

            foreach (var (label, value) in new[] { ("local", role.LocalModel), ("cloud", role.CloudModel) })
            {
                if (!string.IsNullOrWhiteSpace(value) && !ModelConfigurationRules.IsSafeModelName(value.Trim()))
                {
                    return $"The {label} model alias for {role.Role} may use letters, digits and . _ - : / @ only (up to {ModelGatewaySettings.MaxModelNameLength} characters).";
                }
            }

            if (!role.IsEnabled)
            {
                continue;
            }

            var alias = role.Placement == ModelPlacement.Cloud ? role.CloudModel : role.LocalModel;
            if (string.IsNullOrWhiteSpace(alias))
            {
                return $"Set the {role.Placement.ToString().ToLowerInvariant()} model alias for {role.Role} before enabling it.";
            }

            if (role.Placement == ModelPlacement.Cloud && !command.AllowCloud)
            {
                return $"{role.Role} is placed on a cloud model; allow cloud use first, or place it locally.";
            }
        }

        return null;
    }
}
