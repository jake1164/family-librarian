using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Infrastructure.Integrations;

namespace FamilyLibrarian.Web.Endpoints;

internal static class MetadataIntegrationEndpoints
{
    public static void MapMetadataIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        // Built-in provider settings. Every route is Admin-only and addresses a known
        // installed provider id; none accepts an arbitrary target or returns a secret.
        var integrations = app.MapGroup("/api/v1/admin/integrations/metadata")
            .RequireAuthorization("Admin")
            // Cookie authentication means a cross-site request would otherwise arrive
            // already authenticated; every mutation below must carry a matching token.
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        integrations.MapGet("/", ListProvidersAsync);
        integrations.MapPut("/{providerId}/enabled", SetProviderEnabledAsync);
        integrations.MapPut("/{providerId}/credential", SetProviderCredentialAsync);
        integrations.MapDelete("/{providerId}/credential", ClearProviderCredentialAsync);
        integrations.MapPost("/{providerId}/test", TestProviderAsync);
    }

    private static async Task<IResult> ListProvidersAsync(
        ProviderAdminService admin,
        CancellationToken cancellationToken)
    {
        var statuses = await admin.GetStatusesAsync(cancellationToken);
        return Results.Ok(new ProviderListResponse(
            statuses.Select(ToProviderResponse).ToArray()));
    }

    private static async Task<IResult> SetProviderEnabledAsync(
        string providerId,
        SetProviderEnabledRequest request,
        ProviderAdminService admin,
        CancellationToken cancellationToken) =>
        ToResult(await admin.SetEnabledAsync(providerId, request.Enabled, cancellationToken));

    private static async Task<IResult> SetProviderCredentialAsync(
        string providerId,
        SetProviderCredentialRequest request,
        ProviderAdminService admin,
        CancellationToken cancellationToken) =>
        ToResult(await admin.SetCredentialAsync(providerId, request.Credential, cancellationToken));

    private static async Task<IResult> ClearProviderCredentialAsync(
        string providerId,
        ProviderAdminService admin,
        CancellationToken cancellationToken) =>
        ToResult(await admin.ClearCredentialAsync(providerId, cancellationToken));

    private static async Task<IResult> TestProviderAsync(
        string providerId,
        ProviderAdminService admin,
        MetadataProviderConnectionTester tester,
        CancellationToken cancellationToken)
    {
        if (await admin.GetStatusAsync(providerId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        var outcome = await tester.TestAsync(providerId, cancellationToken);
        var recorded = await admin.RecordTestResultAsync(
            providerId,
            outcome.Succeeded,
            outcome.Message,
            cancellationToken);

        return recorded.Status is null
            ? Results.NotFound()
            : Results.Ok(new ProviderTestResponse(
                outcome.Succeeded,
                outcome.Message,
                ToProviderResponse(recorded.Status)));
    }

    private static IResult ToResult(ProviderCommandResult result) => result.Outcome switch
    {
        ProviderCommandOutcome.NotFound => Results.NotFound(),
        ProviderCommandOutcome.Invalid => Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["provider"] = [result.Error ?? "That change is not allowed."]
            }),
        _ => Results.Ok(ToProviderResponse(result.Status!))
    };

    private static ProviderStatusResponse ToProviderResponse(ProviderStatus status) => new(
        status.ProviderId,
        status.DisplayName,
        status.Capabilities
            .Select(capability => capability.ToString())
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray(),
        status.RequiresCredential,
        status.IsEnabled,
        status.HasStoredCredential,
        status.IsExternallyManaged,
        status.IsMisconfigured,
        status.CanManageCredential,
        status.CredentialHint,
        status.CredentialSetAtUtc,
        status.LastTestedAtUtc,
        status.LastTestSucceeded,
        status.LastTestMessage,
        status.SetupInstructions,
        status.SetupLinks
            .Select(link => new ProviderSetupLinkResponse(link.Label, link.Url))
            .ToArray());
}
