using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>The administrative commands behind the External Providers panel — register, configure, test, remove.</summary>
public sealed class ExternalProviderAdminService(
    IExternalProviderStore store,
    ICredentialProtector protector,
    IExternalProviderClient client,
    PrivateEgressRouteResolver routeResolver,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<IReadOnlyList<ExternalProviderStatus>> ListAsync(CancellationToken cancellationToken) =>
        (await store.ListAsync(cancellationToken)).Select(ToStatus).ToArray();

    public async Task<ExternalProviderCommandResult> CreateAsync(
        string providerId, string displayName, string baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 64)
        {
            return ExternalProviderCommandResult.Invalid("Choose a short provider id.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ExternalProviderCommandResult.Invalid("Enter a valid http(s) base URL.");
        }

        if (await store.FindByProviderIdAsync(providerId, cancellationToken) is not null)
        {
            return ExternalProviderCommandResult.Invalid("That provider id is already registered.");
        }

        var provider = new ExternalProvider(providerId, displayName, baseUrl, clock.UtcNow);
        store.Add(provider);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderCreated, AuditSubjectTypes.ExternalProvider, provider.Id.ToString(),
            new { provider.ProviderId }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    public async Task<ExternalProviderCommandResult> SetEnabledAsync(
        Guid id, bool isEnabled, CancellationToken cancellationToken)
    {
        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        provider.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            isEnabled ? AuditActions.ExternalProviderEnabled : AuditActions.ExternalProviderDisabled,
            AuditSubjectTypes.ExternalProvider, id.ToString(), new { provider.ProviderId }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    public async Task<ExternalProviderCommandResult> SetDetailsAsync(
        Guid id, string displayName, string baseUrl, CancellationToken cancellationToken)
    {
        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ExternalProviderCommandResult.Invalid("Enter a valid http(s) base URL.");
        }

        provider.SetDetails(displayName, baseUrl, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderUpdated, AuditSubjectTypes.ExternalProvider, id.ToString(),
            new { provider.ProviderId }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    public async Task<ExternalProviderCommandResult> SetApiKeyAsync(
        Guid id, string apiKey, CancellationToken cancellationToken)
    {
        var trimmed = apiKey?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ExternalProviderCommandResult.Invalid("An API key is required.");
        }

        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        provider.SetApiKey(
            protector.Protect(ExternalProviderSecretPurposes.ApiKey, trimmed),
            protector.FormatVersion,
            trimmed.Length <= 4 ? null : trimmed[^4..],
            currentUser.UserId,
            clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderApiKeySet, AuditSubjectTypes.ExternalProvider, id.ToString(),
            new { provider.ProviderId }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    public async Task<ExternalProviderCommandResult> ClearApiKeyAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        provider.ClearApiKey(currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderApiKeyCleared, AuditSubjectTypes.ExternalProvider, id.ToString(),
            new { provider.ProviderId }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    public async Task<ExternalProviderCommandResult> TestConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        var resolution = routeResolver.Resolve(provider.CachedEgressPolicy);
        if (!resolution.IsAllowed)
        {
            provider.RecordTestResult(
                false, resolution.BlockedReason, provider.CachedProtocolVersion, provider.CachedCapabilities,
                provider.CachedEgressPolicy, currentUser.UserId, clock.UtcNow);
            await store.SaveChangesAsync(cancellationToken);
            return ExternalProviderCommandResult.Success(ToStatus(provider));
        }

        var apiKey = provider.HasApiKey
            ? protector.Unprotect(ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
            : null;

        try
        {
            var manifest = await client.GetManifestAsync(provider.BaseUrl, apiKey, resolution.Route!, cancellationToken);
            var healthy = await client.GetHealthAsync(provider.BaseUrl, apiKey, resolution.Route!, cancellationToken);

            var egressPolicy = ParseEgressPolicy(manifest.EgressPolicy);
            provider.RecordTestResult(
                healthy,
                healthy
                    ? $"Reached {manifest.Name} (protocol v{manifest.ProtocolVersion})."
                    : "The manifest was reachable, but the health check did not report healthy.",
                manifest.ProtocolVersion,
                string.Join(',', manifest.Capabilities),
                egressPolicy,
                currentUser.UserId,
                clock.UtcNow);
        }
        catch (HttpRequestException exception)
        {
            provider.RecordTestResult(
                false, $"The provider is unreachable: {exception.Message}", provider.CachedProtocolVersion,
                provider.CachedCapabilities, provider.CachedEgressPolicy, currentUser.UserId, clock.UtcNow);
        }

        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderTested, AuditSubjectTypes.ExternalProvider, id.ToString(),
            new { provider.ProviderId, provider.LastTestSucceeded }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    public async Task<ExternalProviderCommandResult> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        store.Remove(provider);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderRemoved, AuditSubjectTypes.ExternalProvider, id.ToString(),
            new { provider.ProviderId }, cancellationToken);

        return ExternalProviderCommandResult.Success(status: null);
    }

    /// <summary>
    /// Lets an administrator override a provider's own declared egress policy —
    /// accepted as a deliberate trade-off: a provider that declares
    /// <c>PRIVATE_REQUIRED</c> for a real reason can have that requirement
    /// weakened here, which is exactly why the UI surfaces a warning when the
    /// effective policy ends up less strict than what the provider declared.
    /// </summary>
    public async Task<ExternalProviderCommandResult> SetEgressPolicyOverrideAsync(
        Guid id, EgressPolicy? policy, CancellationToken cancellationToken)
    {
        var provider = await store.FindAsync(id, cancellationToken);
        if (provider is null)
        {
            return ExternalProviderCommandResult.Invalid("That provider no longer exists.");
        }

        provider.SetEgressPolicyOverride(policy, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ExternalProviderEgressPolicyOverrideChanged, AuditSubjectTypes.ExternalProvider, id.ToString(),
            new { provider.ProviderId, Override = policy?.ToString() }, cancellationToken);

        return ExternalProviderCommandResult.Success(ToStatus(provider));
    }

    private static EgressPolicy ParseEgressPolicy(string value) => value.ToUpperInvariant() switch
    {
        "PRIVATE_REQUIRED" => EgressPolicy.PrivateRequired,
        "CUSTOM_PROXY" => EgressPolicy.CustomProxy,
        _ => EgressPolicy.Normal
    };

    private static ExternalProviderStatus ToStatus(ExternalProvider provider) => new(
        provider.Id,
        provider.ProviderId,
        provider.DisplayName,
        provider.BaseUrl,
        provider.IsEnabled,
        provider.HasApiKey,
        provider.ApiKeyHint,
        provider.ApiKeySetAtUtc,
        provider.CachedProtocolVersion,
        provider.CachedCapabilities,
        provider.CachedEgressPolicy.ToString(),
        provider.EgressPolicyOverride?.ToString(),
        provider.EffectiveEgressPolicy.ToString(),
        provider.LastTestedAtUtc,
        provider.LastTestSucceeded,
        provider.LastTestMessage);
}

public sealed record ExternalProviderStatus(
    Guid Id,
    string ProviderId,
    string DisplayName,
    string BaseUrl,
    bool IsEnabled,
    bool HasApiKey,
    string? ApiKeyHint,
    DateTimeOffset? ApiKeySetAtUtc,
    string? CachedProtocolVersion,
    string? CachedCapabilities,
    string CachedEgressPolicy,
    string? EgressPolicyOverride,
    string EffectiveEgressPolicy,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record ExternalProviderCommandResult(
    ExternalProviderCommandOutcome Outcome, ExternalProviderStatus? Status, string? Error)
{
    public static ExternalProviderCommandResult Success(ExternalProviderStatus? status) =>
        new(ExternalProviderCommandOutcome.Success, status, null);

    public static ExternalProviderCommandResult Invalid(string error) =>
        new(ExternalProviderCommandOutcome.Invalid, null, error);
}

public enum ExternalProviderCommandOutcome
{
    Success,
    Invalid
}
