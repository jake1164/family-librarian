using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.ModelTesting;

namespace FamilyLibrarian.Application.ModelTesting;

public interface IModelTestSettingsStore
{
    Task<ModelTestSettings?> FindAsync(CancellationToken cancellationToken);

    Task<ModelTestSettings> GetOrCreateAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Fixed data-protection purpose for the stored model API key.</summary>
public static class ModelTestSecretPurposes
{
    public const string ApiKey = "model-test-api-key";
}

/// <summary>The settings as an administrator may see them. The API key is never included, only whether one is set.</summary>
public sealed record ModelTestSettingsView(
    bool IsEnabled,
    string? EndpointUrl,
    bool HasApiKey,
    DateTimeOffset? ApiKeySetAtUtc,
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens);

public sealed record SaveModelTestSettings(
    string? EndpointUrl,
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens);

public sealed record ModelTestSettingsResult(bool Succeeded, bool Forbidden, string? Error, ModelTestSettingsView? Settings)
{
    public static ModelTestSettingsResult Success(ModelTestSettingsView settings) => new(true, false, null, settings);

    public static ModelTestSettingsResult Invalid(string error) => new(false, false, error, null);

    public static ModelTestSettingsResult NotAllowed() => new(false, true, "Administrator access is required.", null);
}

/// <summary>
/// Administrator configuration of the model test harness. Every operation checks the caller is an authenticated
/// administrator itself, not only the route, so it is safe to call from any transport.
/// </summary>
public sealed class ModelTestSettingsService(
    IModelTestSettingsStore store,
    ICredentialProtector protector,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ICurrentAdministrator administrator,
    IClock clock)
{
    private const string SubjectId = "model-test";

    public async Task<ModelTestSettingsResult> GetAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelTestSettingsResult.NotAllowed();
        }

        return ModelTestSettingsResult.Success(ToView(await store.FindAsync(cancellationToken)));
    }

    public async Task<ModelTestSettingsResult> SaveAsync(SaveModelTestSettings command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdministrator())
        {
            return ModelTestSettingsResult.NotAllowed();
        }

        if (Validate(command) is { } error)
        {
            return ModelTestSettingsResult.Invalid(error);
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.Configure(
            command.EndpointUrl, command.PrimaryModel, command.ComparisonModel, command.CloudModel, command.AllowCloud,
            command.TimeoutSeconds, command.MaxOutputTokens, currentUser.UserId, clock.UtcNow);
        if (settings.IsEnabled && GetConfigurationError(settings) is not null)
        {
            // A configuration that can no longer run must not stay "enabled".
            settings.SetEnabled(false, currentUser.UserId, clock.UtcNow);
        }

        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelTestSettingsChanged, AuditSubjectTypes.ModelTest, SubjectId,
            new { settings.AllowCloud, settings.TimeoutSeconds, settings.MaxOutputTokens }, cancellationToken);
        return ModelTestSettingsResult.Success(ToView(settings));
    }

    public async Task<ModelTestSettingsResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelTestSettingsResult.NotAllowed();
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        if (enabled && GetConfigurationError(settings) is { } error)
        {
            return ModelTestSettingsResult.Invalid(error);
        }

        settings.SetEnabled(enabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelTestSettingsChanged, AuditSubjectTypes.ModelTest, SubjectId, new { Enabled = enabled }, cancellationToken);
        return ModelTestSettingsResult.Success(ToView(settings));
    }

    public async Task<ModelTestSettingsResult> SetApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelTestSettingsResult.NotAllowed();
        }

        var trimmed = apiKey?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2_048 || trimmed.Any(char.IsControl))
        {
            return ModelTestSettingsResult.Invalid("Enter an API key (up to 2,048 characters, no control characters).");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetApiKey(protector.Protect(ModelTestSecretPurposes.ApiKey, trimmed), protector.FormatVersion, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelTestSecretSet, AuditSubjectTypes.ModelTest, SubjectId, new { Field = "api_key" }, cancellationToken);
        return ModelTestSettingsResult.Success(ToView(settings));
    }

    public async Task<ModelTestSettingsResult> ClearApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelTestSettingsResult.NotAllowed();
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.ClearApiKey(currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelTestSecretCleared, AuditSubjectTypes.ModelTest, SubjectId, new { Field = "api_key" }, cancellationToken);
        return ModelTestSettingsResult.Success(ToView(settings));
    }

    internal static string? GetConfigurationError(ModelTestSettings settings) =>
        settings.EndpointUrl is null ? "Set the endpoint URL first."
        : settings.PrimaryModel is null ? "Set the primary model alias first."
        : null;

    internal static ModelTestSettingsView ToView(ModelTestSettings? settings) => settings is null
        ? new ModelTestSettingsView(false, null, false, null, null, null, null, false, ModelTestSettings.DefaultTimeoutSeconds, ModelTestSettings.DefaultMaxOutputTokens)
        : new ModelTestSettingsView(
            settings.IsEnabled, settings.EndpointUrl, settings.HasApiKey, settings.ApiKeySetAtUtc, settings.PrimaryModel,
            settings.ComparisonModel, settings.CloudModel, settings.AllowCloud, settings.TimeoutSeconds, settings.MaxOutputTokens);

    private bool IsAdministrator() => currentUser.UserId is not null && administrator.IsAdministrator;

    private static string? Validate(SaveModelTestSettings command)
    {
        if (!string.IsNullOrWhiteSpace(command.EndpointUrl))
        {
            var text = command.EndpointUrl.Trim();
            if (text.Length > ModelTestSettings.MaxEndpointLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                return "The endpoint URL is not valid.";
            }

            if (uri.Scheme is not ("http" or "https"))
            {
                return "The endpoint URL must use http or https.";
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return "The endpoint URL must not contain credentials, a query string or a fragment. Use the API key field.";
            }
        }

        foreach (var (label, value) in new[] { ("primary", command.PrimaryModel), ("comparison", command.ComparisonModel), ("cloud", command.CloudModel) })
        {
            if (!string.IsNullOrWhiteSpace(value) && !IsSafeModelName(value.Trim()))
            {
                return $"The {label} model alias may use letters, digits and . _ - : / @ only (up to {ModelTestSettings.MaxModelNameLength} characters).";
            }
        }

        if (command.TimeoutSeconds is < ModelTestSettings.MinimumTimeoutSeconds or > ModelTestSettings.MaximumTimeoutSeconds)
        {
            return $"The timeout must be {ModelTestSettings.MinimumTimeoutSeconds}-{ModelTestSettings.MaximumTimeoutSeconds} seconds.";
        }

        if (command.MaxOutputTokens is < ModelTestSettings.MinimumMaxOutputTokens or > ModelTestSettings.MaximumMaxOutputTokens)
        {
            return $"Maximum output tokens must be {ModelTestSettings.MinimumMaxOutputTokens}-{ModelTestSettings.MaximumMaxOutputTokens}.";
        }

        if (command.AllowCloud && string.IsNullOrWhiteSpace(command.CloudModel))
        {
            return "Choose the cloud model alias before allowing cloud tests.";
        }

        return null;
    }

    public static bool IsSafeModelName(string value) =>
        value.Length is > 0 and <= ModelTestSettings.MaxModelNameLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/' or '@');
}
