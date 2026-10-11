using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

public enum ModelUnavailableReason
{
    None = 0,
    GatewayDisabled,
    RoleDisabled,
    NotConfigured,
    CloudNotAllowed,
    ApiKeyUnreadable
}

/// <summary>
/// Everything needed to call one role's model. <see cref="ApiKey"/> is a secret: it must never be logged, audited,
/// persisted or returned from an endpoint.
/// </summary>
public sealed record ModelRoleResolution(
    ModelRole Role,
    ModelUnavailableReason Reason,
    Uri? Endpoint,
    string? ApiKey,
    string? Model,
    bool IsCloud,
    ModelExecutionLimits Limits,
    bool AllowCloudExcerpts = false)
{
    public bool IsAvailable => Reason == ModelUnavailableReason.None;

    public static ModelRoleResolution Unavailable(ModelRole role, ModelUnavailableReason reason) => new(role, reason, null, null, null, false, ModelExecutionLimits.Default);
}

/// <summary>
/// Decides, from the current settings, whether a role may call a model and which one. Resolved fresh for every call
/// because an administrator can change or disable it at any moment.
/// </summary>
public interface IModelRoleResolver
{
    Task<ModelRoleResolution> ResolveAsync(ModelRole role, CancellationToken cancellationToken);
}

/// <summary>
/// A role resolves only to the alias of its chosen placement. A cloud placement is refused unless cloud use is allowed, and
/// there is no path from one placement to the other: an unavailable local model never "falls back" to a cloud one.
/// </summary>
public sealed class ModelRoleResolver(IModelGatewaySettingsStore store, ICredentialProtector protector) : IModelRoleResolver
{
    public async Task<ModelRoleResolution> ResolveAsync(ModelRole role, CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            return ModelRoleResolution.Unavailable(role, ModelUnavailableReason.GatewayDisabled);
        }

        var setting = settings.FindRole(role);
        if (setting is null || !setting.IsEnabled)
        {
            return ModelRoleResolution.Unavailable(role, ModelUnavailableReason.RoleDisabled);
        }

        var isCloud = setting.Placement == ModelPlacement.Cloud;
        if (isCloud && !settings.AllowCloud)
        {
            return ModelRoleResolution.Unavailable(role, ModelUnavailableReason.CloudNotAllowed);
        }

        if (settings.EndpointUrl is null || setting.ActiveModel is null || !Uri.TryCreate(settings.EndpointUrl, UriKind.Absolute, out var endpoint))
        {
            return ModelRoleResolution.Unavailable(role, ModelUnavailableReason.NotConfigured);
        }

        string? apiKey = null;
        if (settings.HasApiKey)
        {
            apiKey = protector.Unprotect(ModelGatewaySecretPurposes.ApiKey, settings.ProtectedApiKey!, settings.ApiKeyFormatVersion);
            if (apiKey is null)
            {
                return ModelRoleResolution.Unavailable(role, ModelUnavailableReason.ApiKeyUnreadable);
            }
        }

        return new ModelRoleResolution(role, ModelUnavailableReason.None, endpoint, apiKey, setting.ActiveModel, isCloud, settings.Limits, isCloud && settings.AllowCloudExcerpts);
    }
}
