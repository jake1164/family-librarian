using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>The administrative commands behind the Private Acquisition Network panel, mirroring <c>OidcSettingsService</c>'s shape.</summary>
public sealed class PrivateEgressGatewayService(
    IPrivateEgressGatewayStore store,
    IPrivateEgressGatewayTester tester,
    IPrivateEgressGatewayRuntimeCache runtimeCache,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<PrivateEgressGatewayStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return ToStatus(settings);
    }

    public async Task<PrivateEgressGatewayRuntimeState> LoadRuntimeStateAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return ToRuntimeState(settings);
    }

    public async Task<PrivateEgressGatewayCommandResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(
            AuditActions.PrivateEgressGatewayChanged, AuditSubjectTypes.PrivateEgressGateway, null,
            new { Enabled = isEnabled }, cancellationToken);

        return PrivateEgressGatewayCommandResult.Success(ToStatus(settings));
    }

    public async Task<PrivateEgressGatewayCommandResult> SetGatewayEndpointAsync(
        string? gatewayEndpoint, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(gatewayEndpoint) &&
            (!Uri.TryCreate(gatewayEndpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return PrivateEgressGatewayCommandResult.Invalid("Enter a valid http(s) proxy endpoint.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetGatewayEndpoint(gatewayEndpoint, currentUser.UserId, clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(
            AuditActions.PrivateEgressGatewayChanged, AuditSubjectTypes.PrivateEgressGateway, null,
            new { }, cancellationToken);

        return PrivateEgressGatewayCommandResult.Success(ToStatus(settings));
    }

    public async Task<PrivateEgressGatewayCommandResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetOrCreateAsync(cancellationToken);
        var outcome = await tester.TestAsync(settings.GatewayEndpoint, cancellationToken);

        settings.RecordTestResult(outcome.Succeeded, outcome.Message, currentUser.UserId, clock.UtcNow);
        await SaveAndRefreshAsync(settings, cancellationToken);

        await audit.WriteAsync(
            AuditActions.PrivateEgressGatewayTested, AuditSubjectTypes.PrivateEgressGateway, null,
            new { outcome.Succeeded }, cancellationToken);

        return PrivateEgressGatewayCommandResult.Success(ToStatus(settings));
    }

    private async Task SaveAndRefreshAsync(PrivateEgressGatewaySettings settings, CancellationToken cancellationToken)
    {
        await store.SaveChangesAsync(cancellationToken);
        runtimeCache.Refresh(ToRuntimeState(settings));
    }

    private static PrivateEgressGatewayRuntimeState ToRuntimeState(PrivateEgressGatewaySettings? settings) =>
        settings is null
            ? PrivateEgressGatewayRuntimeState.Disabled
            : new PrivateEgressGatewayRuntimeState(settings.IsEnabled, settings.GatewayEndpoint, settings.LastTestSucceeded == true);

    private static PrivateEgressGatewayStatus ToStatus(PrivateEgressGatewaySettings? settings) => settings is null
        ? new PrivateEgressGatewayStatus(false, null, null, null, null)
        : new PrivateEgressGatewayStatus(
            settings.IsEnabled,
            settings.GatewayEndpoint,
            settings.LastTestedAtUtc,
            settings.LastTestSucceeded,
            settings.LastTestMessage);
}

public interface IPrivateEgressGatewayTester
{
    Task<GatewayTestOutcome> TestAsync(string? gatewayEndpoint, CancellationToken cancellationToken);
}

public sealed record GatewayTestOutcome(bool Succeeded, string Message)
{
    public static GatewayTestOutcome Success(string message) => new(true, message);

    public static GatewayTestOutcome Failure(string message) => new(false, message);
}

public sealed record PrivateEgressGatewayStatus(
    bool IsEnabled,
    string? GatewayEndpoint,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record PrivateEgressGatewayCommandResult(
    PrivateEgressGatewayCommandOutcome Outcome, PrivateEgressGatewayStatus? Status, string? Error)
{
    public static PrivateEgressGatewayCommandResult Success(PrivateEgressGatewayStatus status) =>
        new(PrivateEgressGatewayCommandOutcome.Success, status, null);

    public static PrivateEgressGatewayCommandResult Invalid(string error) =>
        new(PrivateEgressGatewayCommandOutcome.Invalid, null, error);
}

public enum PrivateEgressGatewayCommandOutcome
{
    Success,
    Invalid
}
