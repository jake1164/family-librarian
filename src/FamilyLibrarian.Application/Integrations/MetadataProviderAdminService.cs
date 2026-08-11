using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Integrations;

/// <summary>
/// The administrative commands behind the Admin Metadata Integrations surface.
/// </summary>
/// <remarks>
/// Every method here is authorized as Admin at the endpoint boundary. This type
/// enforces the rules that must hold regardless of which caller reaches it: a
/// stored credential is never returned, an externally managed credential is never
/// overwritten, and each mutation writes a secret-free audit event.
/// </remarks>
public sealed class MetadataProviderAdminService(
    IMetadataProviderRegistry registry,
    IMetadataProviderSettingsStore store,
    ICredentialProtector protector,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<IReadOnlyList<MetadataProviderStatus>> GetStatusesAsync(
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAllAsync(cancellationToken);
        var byProviderId = settings.ToDictionary(
            setting => setting.ProviderId,
            StringComparer.OrdinalIgnoreCase);

        return registry.GetInstalledProviders()
            .Select(descriptor =>
            {
                byProviderId.TryGetValue(descriptor.Id, out var setting);
                return ToStatus(descriptor, setting);
            })
            .ToArray();
    }

    public async Task<MetadataProviderStatus?> GetStatusAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(providerId);
        if (descriptor is null)
        {
            return null;
        }

        var setting = await store.FindAsync(descriptor.Id, cancellationToken);
        return ToStatus(descriptor, setting);
    }

    public async Task<MetadataProviderCommandResult> SetEnabledAsync(
        string providerId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(providerId);
        if (descriptor is null)
        {
            return MetadataProviderCommandResult.NotFound();
        }

        var setting = await store.GetOrCreateAsync(descriptor.Id, cancellationToken);

        // Refuse to enable a credentialed provider that has no usable credential.
        // Allowing it would produce a provider that looks on but silently fails
        // every search, which is worse than a clear rejection.
        if (isEnabled &&
            descriptor.RequiresCredential &&
            !descriptor.HasExternallyManagedCredential &&
            !setting.HasStoredCredential)
        {
            return MetadataProviderCommandResult.Invalid(
                $"{descriptor.DisplayName} needs an API key before it can be enabled.");
        }

        setting.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            isEnabled ? AuditActions.MetadataProviderEnabled : AuditActions.MetadataProviderDisabled,
            AuditSubjectTypes.MetadataProvider,
            descriptor.Id,
            new { descriptor.Id, Enabled = isEnabled },
            cancellationToken);

        return MetadataProviderCommandResult.Success(ToStatus(descriptor, setting));
    }

    public async Task<MetadataProviderCommandResult> SetCredentialAsync(
        string providerId,
        string credential,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(providerId);
        if (descriptor is null)
        {
            return MetadataProviderCommandResult.NotFound();
        }

        if (!descriptor.RequiresCredential)
        {
            return MetadataProviderCommandResult.Invalid(
                $"{descriptor.DisplayName} does not use an API key.");
        }

        if (descriptor.HasExternallyManagedCredential)
        {
            return MetadataProviderCommandResult.Invalid(
                $"{descriptor.DisplayName} is configured by this deployment. Change it where the deployment secret is managed.");
        }

        var trimmed = credential?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return MetadataProviderCommandResult.Invalid("An API key is required.");
        }

        if (trimmed.Length > 512)
        {
            return MetadataProviderCommandResult.Invalid("That API key is longer than this field allows.");
        }

        var setting = await store.GetOrCreateAsync(descriptor.Id, cancellationToken);
        setting.SetCredential(
            protector.Protect(descriptor.Id, trimmed),
            protector.FormatVersion,
            BuildHint(trimmed),
            currentUser.UserId,
            clock.UtcNow);

        await store.SaveChangesAsync(cancellationToken);

        // The audit detail records that a key was set, never the key itself.
        await audit.WriteAsync(
            AuditActions.MetadataProviderCredentialSet,
            AuditSubjectTypes.MetadataProvider,
            descriptor.Id,
            new { descriptor.Id, HasCredential = true },
            cancellationToken);

        return MetadataProviderCommandResult.Success(ToStatus(descriptor, setting));
    }

    public async Task<MetadataProviderCommandResult> ClearCredentialAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(providerId);
        if (descriptor is null)
        {
            return MetadataProviderCommandResult.NotFound();
        }

        if (descriptor.HasExternallyManagedCredential)
        {
            return MetadataProviderCommandResult.Invalid(
                $"{descriptor.DisplayName} is configured by this deployment. Change it where the deployment secret is managed.");
        }

        var setting = await store.GetOrCreateAsync(descriptor.Id, cancellationToken);
        setting.ClearCredential(currentUser.UserId, clock.UtcNow);

        // Clearing the key strands a credentialed provider, so switch it off in
        // the same transaction rather than leaving it enabled-but-broken.
        if (descriptor.RequiresCredential && setting.IsEnabled)
        {
            setting.SetEnabled(false, currentUser.UserId, clock.UtcNow);
        }

        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.MetadataProviderCredentialCleared,
            AuditSubjectTypes.MetadataProvider,
            descriptor.Id,
            new { descriptor.Id, HasCredential = false },
            cancellationToken);

        return MetadataProviderCommandResult.Success(ToStatus(descriptor, setting));
    }

    /// <summary>
    /// Records the outcome of a server-side connection test. The caller performs
    /// the probe; this persists and audits the result.
    /// </summary>
    public async Task<MetadataProviderCommandResult> RecordTestResultAsync(
        string providerId,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(providerId);
        if (descriptor is null)
        {
            return MetadataProviderCommandResult.NotFound();
        }

        var setting = await store.GetOrCreateAsync(descriptor.Id, cancellationToken);
        setting.RecordTestResult(succeeded, message, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.MetadataProviderTested,
            AuditSubjectTypes.MetadataProvider,
            descriptor.Id,
            new { descriptor.Id, Succeeded = succeeded },
            cancellationToken);

        return MetadataProviderCommandResult.Success(ToStatus(descriptor, setting));
    }

    private static MetadataProviderStatus ToStatus(
        MetadataProviderDescriptor descriptor,
        Domain.Integrations.MetadataProviderSetting? setting) => new(
            descriptor.Id,
            descriptor.DisplayName,
            descriptor.RequiresCredential,
            // Shared with the search path so this page cannot claim a provider is
            // on while search skips it.
            MetadataProviderState.IsEnabled(descriptor, setting),
            // Deliberately not MetadataProviderState.HasCredential: that returns
            // true for keyless providers, which would show "key stored" against
            // Open Library. This is strictly "a secret exists for this provider".
            descriptor.HasExternallyManagedCredential || (setting?.HasStoredCredential ?? false),
            descriptor.HasExternallyManagedCredential,
            descriptor.HasExternallyManagedCredential ? null : setting?.CredentialHint,
            descriptor.HasExternallyManagedCredential ? null : setting?.CredentialSetAtUtc,
            setting?.LastTestedAtUtc,
            setting?.LastTestSucceeded,
            setting?.LastTestMessage);

    private static string? BuildHint(string credential) =>
        credential.Length <= 4 ? null : credential[^4..];
}

public sealed record MetadataProviderCommandResult(
    MetadataProviderCommandOutcome Outcome,
    MetadataProviderStatus? Status,
    string? Error)
{
    public static MetadataProviderCommandResult Success(MetadataProviderStatus status) =>
        new(MetadataProviderCommandOutcome.Success, status, null);

    public static MetadataProviderCommandResult NotFound() =>
        new(MetadataProviderCommandOutcome.NotFound, null, null);

    public static MetadataProviderCommandResult Invalid(string error) =>
        new(MetadataProviderCommandOutcome.Invalid, null, error);
}

public enum MetadataProviderCommandOutcome
{
    Success,
    NotFound,
    Invalid
}
