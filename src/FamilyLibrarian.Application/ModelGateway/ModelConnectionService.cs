using System.Security.Cryptography;
using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.ModelTesting;

namespace FamilyLibrarian.Application.ModelGateway;

public enum ModelConnectionReconciliation
{
    /// <summary>The older test harness holds no endpoint or key of its own.</summary>
    NothingToDo = 0,

    /// <summary>The test harness's endpoint and/or key were moved to the shared connection (which had none).</summary>
    Migrated,

    /// <summary>The test harness held the same connection as the shared one; its duplicate copy was removed.</summary>
    DuplicateRemoved,

    /// <summary>The two disagree. Both are preserved untouched until an administrator chooses.</summary>
    Conflict
}

/// <summary>
/// What the administrator needs to resolve a disagreement. Never contains a key; the endpoints are not secrets (they carry no
/// credentials, query string or fragment by validation).
/// </summary>
public sealed record ModelConnectionNotice(
    bool HasConflict,
    string? SharedEndpointUrl,
    bool SharedHasApiKey,
    string? LegacyTestEndpointUrl,
    bool LegacyTestHasApiKey,
    string? Explanation);

public enum ModelConnectionChoice
{
    /// <summary>Keep the production gateway's connection and discard the test harness's older copy.</summary>
    Shared = 1,

    /// <summary>Make the test harness's older connection the shared one. Production AI is switched off until re-enabled.</summary>
    LegacyTest = 2
}

public sealed record ModelConnectionResult(bool Succeeded, bool Forbidden, string? Error, ModelConnectionNotice? Notice)
{
    public static ModelConnectionResult Success(ModelConnectionNotice notice) => new(true, false, null, notice);

    public static ModelConnectionResult Invalid(string error) => new(false, false, error, null);

    public static ModelConnectionResult NotAllowed() => new(false, true, "Administrator access is required.", null);
}

/// <summary>
/// The one shared LiteLLM / OpenAI-compatible connection (endpoint and protected API key) used by both the production model
/// gateway and the administrator's test harness. Alpha.6 first shipped these as two separate configurations (M3-5 and M4-0), so
/// an upgraded installation may hold a connection in each. This service reconciles them under fixed, documented rules and
/// never silently chooses between two different ones:
/// <list type="bullet">
/// <item>Only the test harness has a connection: it moves to the shared connection. Production AI stays exactly as it was (off
/// unless an administrator had already turned it on, and no role or cloud permission is touched).</item>
/// <item>Both have the same endpoint and key: the test harness's duplicate is removed.</item>
/// <item>Both have a connection and they differ (endpoint, or key at the same endpoint, or a key that cannot be read): both are
/// preserved untouched, tests keep using the harness's own older connection, and an administrator resolves it explicitly.</item>
/// </list>
/// It is also the test harness's read-only window onto the gateway (<see cref="IModelTestTargetSource"/>): it can look at a
/// role's alias and placement, and cannot change a role, a switch, a limit or a permission.
/// </summary>
public sealed class ModelConnectionService(
    IModelGatewaySettingsStore gatewayStore,
    IModelTestSettingsStore testStore,
    ICredentialProtector protector,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ICurrentAdministrator administrator,
    IClock clock) : IModelTestTargetSource
{
    private const string SubjectId = "model-gateway";

    public async Task<ModelConnectionResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelConnectionResult.NotAllowed();
        }

        await ReconcileCoreAsync(cancellationToken);
        return ModelConnectionResult.Success(await NoticeAsync(cancellationToken));
    }

    public async Task<ModelConnectionResult> ResolveAsync(ModelConnectionChoice choice, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return ModelConnectionResult.NotAllowed();
        }

        if (!Enum.IsDefined(choice))
        {
            return ModelConnectionResult.Invalid("Choose which connection to keep.");
        }

        var legacy = await testStore.FindAsync(cancellationToken);
        if (legacy is null || (legacy.EndpointUrl is null && !legacy.HasApiKey))
        {
            return ModelConnectionResult.Success(await NoticeAsync(cancellationToken));
        }

        var testSettings = await testStore.GetOrCreateAsync(cancellationToken);
        var gateway = await gatewayStore.GetOrCreateAsync(cancellationToken);
        if (choice == ModelConnectionChoice.LegacyTest)
        {
            string? legacyKey = null;
            if (testSettings.HasApiKey)
            {
                legacyKey = protector.Unprotect(ModelTestSecretPurposes.ApiKey, testSettings.ProtectedApiKey!, testSettings.ApiKeyFormatVersion);
                if (legacyKey is null)
                {
                    return ModelConnectionResult.Invalid("The older test key cannot be read, so it cannot be kept. Keep the shared connection, or enter the key again there.");
                }
            }

            gateway.SetEndpoint(testSettings.EndpointUrl, currentUser.UserId, clock.UtcNow);
            if (legacyKey is null)
            {
                gateway.ClearApiKey(currentUser.UserId, clock.UtcNow);
            }
            else
            {
                gateway.SetApiKey(protector.Protect(ModelGatewaySecretPurposes.ApiKey, legacyKey), protector.FormatVersion, currentUser.UserId, clock.UtcNow);
            }

            // Replacing the production connection is a production change: AI stays off until an administrator turns it back on.
            gateway.SetEnabled(false, currentUser.UserId, clock.UtcNow);
        }

        testSettings.ClearConnection(currentUser.UserId, clock.UtcNow);
        await gatewayStore.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelConnectionResolved, AuditSubjectTypes.ModelGateway, SubjectId,
            new { Kept = choice.ToString(), ProductionAiSwitchedOff = choice == ModelConnectionChoice.LegacyTest }, cancellationToken);
        return ModelConnectionResult.Success(await NoticeAsync(cancellationToken));
    }

    public async Task<ModelConnectionNotice> NoticeAsync(CancellationToken cancellationToken)
    {
        var gateway = await gatewayStore.FindAsync(cancellationToken);
        var legacy = await testStore.FindAsync(cancellationToken);
        var legacyPresent = legacy is not null && (legacy.EndpointUrl is not null || legacy.HasApiKey);
        var conflict = legacyPresent && Compare(gateway, legacy!).Outcome == ModelConnectionReconciliation.Conflict;
        return new ModelConnectionNotice(
            conflict,
            gateway?.EndpointUrl,
            gateway?.HasApiKey ?? false,
            legacyPresent ? legacy!.EndpointUrl : null,
            legacyPresent && legacy!.HasApiKey,
            conflict
                ? "The model test page and the AI models page each had their own connection, and they differ. Both are kept until you choose. Tests use the older test connection meanwhile; production AI is unaffected."
                : null);
    }

    // ---- IModelTestTargetSource (read-only) ----

    public async Task<ModelTestConnectionInfo> GetConnectionAsync(CancellationToken cancellationToken)
    {
        await ReconcileCoreAsync(cancellationToken);
        var gateway = await gatewayStore.FindAsync(cancellationToken);
        var legacy = await testStore.FindAsync(cancellationToken);
        var legacyPresent = legacy is not null && (legacy.EndpointUrl is not null || legacy.HasApiKey);
        var useLegacy = legacyPresent && Compare(gateway, legacy!).Outcome == ModelConnectionReconciliation.Conflict;

        var endpointText = useLegacy ? legacy!.EndpointUrl : gateway?.EndpointUrl;
        if (endpointText is null || !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            return new ModelTestConnectionInfo(null, null, useLegacy, "Set the endpoint on the Models & roles tab first.");
        }

        string? apiKey = null;
        var hasKey = useLegacy ? legacy!.HasApiKey : gateway?.HasApiKey ?? false;
        if (hasKey)
        {
            apiKey = useLegacy
                ? protector.Unprotect(ModelTestSecretPurposes.ApiKey, legacy!.ProtectedApiKey!, legacy.ApiKeyFormatVersion)
                : protector.Unprotect(ModelGatewaySecretPurposes.ApiKey, gateway!.ProtectedApiKey!, gateway.ApiKeyFormatVersion);
            if (apiKey is null)
            {
                return new ModelTestConnectionInfo(null, null, useLegacy, "The stored API key could not be decrypted. Enter it again.");
            }
        }

        return new ModelTestConnectionInfo(endpoint, apiKey, useLegacy, null);
    }

    public async Task<ModelTestRoleTarget?> FindRoleTargetAsync(ModelRole role, CancellationToken cancellationToken)
    {
        var gateway = await gatewayStore.FindAsync(cancellationToken);
        var setting = gateway?.FindRole(role);
        return setting?.ActiveModel is { } alias ? new ModelTestRoleTarget(role, alias, setting.Placement == ModelPlacement.Cloud) : null;
    }

    // ---- reconciliation ----

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var legacy = await testStore.FindAsync(cancellationToken);
        if (legacy is null || (legacy.EndpointUrl is null && !legacy.HasApiKey))
        {
            return;
        }

        var gateway = await gatewayStore.FindAsync(cancellationToken);
        var comparison = Compare(gateway, legacy);
        if (comparison.Outcome is ModelConnectionReconciliation.NothingToDo or ModelConnectionReconciliation.Conflict)
        {
            return;
        }

        var testSettings = await testStore.GetOrCreateAsync(cancellationToken);
        if (comparison.Outcome == ModelConnectionReconciliation.Migrated)
        {
            var target = await gatewayStore.GetOrCreateAsync(cancellationToken);
            if (comparison.MoveEndpoint)
            {
                target.SetEndpoint(testSettings.EndpointUrl, currentUser.UserId, clock.UtcNow);
            }

            if (comparison.MoveKey is { } key)
            {
                // Re-protected under the gateway's own purpose; the plaintext lives only in this scope.
                target.SetApiKey(protector.Protect(ModelGatewaySecretPurposes.ApiKey, key), protector.FormatVersion, currentUser.UserId, clock.UtcNow);
            }
        }

        testSettings.ClearConnection(currentUser.UserId, clock.UtcNow);
        await gatewayStore.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.ModelConnectionReconciled, AuditSubjectTypes.ModelGateway, SubjectId,
            new { Outcome = comparison.Outcome.ToString(), MovedEndpoint = comparison.MoveEndpoint, MovedKey = comparison.MoveKey is not null },
            cancellationToken);
    }

    private sealed record Comparison(ModelConnectionReconciliation Outcome, bool MoveEndpoint = false, string? MoveKey = null);

    private Comparison Compare(ModelGatewaySettings? shared, ModelTestSettings legacy)
    {
        var legacyEndpoint = legacy.EndpointUrl;
        var sharedEndpoint = shared?.EndpointUrl;
        var conflict = new Comparison(ModelConnectionReconciliation.Conflict);

        string? legacyKey = null;
        if (legacy.HasApiKey)
        {
            legacyKey = protector.Unprotect(ModelTestSecretPurposes.ApiKey, legacy.ProtectedApiKey!, legacy.ApiKeyFormatVersion);
            if (legacyKey is null)
            {
                return conflict;
            }
        }

        string? sharedKey = null;
        if (shared?.HasApiKey == true)
        {
            sharedKey = protector.Unprotect(ModelGatewaySecretPurposes.ApiKey, shared.ProtectedApiKey!, shared.ApiKeyFormatVersion);
            if (sharedKey is null)
            {
                return conflict;
            }
        }

        bool sameEndpoint = legacyEndpoint is null || (sharedEndpoint is not null && string.Equals(legacyEndpoint, sharedEndpoint, StringComparison.OrdinalIgnoreCase));
        if (!sameEndpoint && sharedEndpoint is not null)
        {
            return conflict;
        }

        // From here the shared endpoint is either the same as the legacy one, or empty and free to receive it.
        var moveEndpoint = legacyEndpoint is not null && sharedEndpoint is null;
        if (legacyKey is null)
        {
            return moveEndpoint ? new Comparison(ModelConnectionReconciliation.Migrated, true) : new Comparison(ModelConnectionReconciliation.DuplicateRemoved);
        }

        if (sharedKey is null)
        {
            return new Comparison(ModelConnectionReconciliation.Migrated, moveEndpoint, legacyKey);
        }

        return KeysEqual(legacyKey, sharedKey)
            ? (moveEndpoint ? new Comparison(ModelConnectionReconciliation.Migrated, true) : new Comparison(ModelConnectionReconciliation.DuplicateRemoved))
            : conflict;
    }

    private static bool KeysEqual(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private bool IsAdministrator() => currentUser.UserId is not null && administrator.IsAdministrator;
}
