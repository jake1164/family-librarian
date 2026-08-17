using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Providers;

namespace FamilyLibrarian.Web.Endpoints;

internal static class PrivateEgressGatewayEndpoints
{
    public static void MapPrivateEgressGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var adminGateway = app.MapGroup("/api/v1/admin/private-egress-gateway")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminGateway.MapGet("/", GetGatewaySettingsAsync);
        adminGateway.MapPut("/enabled", SetGatewayEnabledAsync);
        adminGateway.MapPut("/endpoint", SetGatewayEndpointAsync);
        adminGateway.MapPost("/test", TestGatewayAsync);
    }

    private static async Task<IResult> GetGatewaySettingsAsync(
        PrivateEgressGatewayService service, CancellationToken cancellationToken) =>
        Results.Ok(ToGatewayResponse(await service.GetStatusAsync(cancellationToken)));

    private static async Task<IResult> SetGatewayEnabledAsync(
        SetPrivateEgressGatewayEnabledRequest request, PrivateEgressGatewayService service,
        CancellationToken cancellationToken) =>
        ToGatewayResult(await service.SetEnabledAsync(request.Enabled, cancellationToken));

    private static async Task<IResult> SetGatewayEndpointAsync(
        SetPrivateEgressGatewayEndpointRequest request, PrivateEgressGatewayService service,
        CancellationToken cancellationToken) =>
        ToGatewayResult(await service.SetGatewayEndpointAsync(request.GatewayEndpoint, cancellationToken));

    private static async Task<IResult> TestGatewayAsync(
        PrivateEgressGatewayService service, CancellationToken cancellationToken) =>
        ToGatewayResult(await service.TestConnectionAsync(cancellationToken));

    private static IResult ToGatewayResult(PrivateEgressGatewayCommandResult result) => result.Outcome switch
    {
        PrivateEgressGatewayCommandOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["gateway"] = [result.Error ?? "That change is not allowed."]
        }),
        _ => Results.Ok(ToGatewayResponse(result.Status!))
    };

    private static PrivateEgressGatewayResponse ToGatewayResponse(PrivateEgressGatewayStatus status) => new(
        status.IsEnabled, status.GatewayEndpoint, status.LastTestedAtUtc, status.LastTestSucceeded, status.LastTestMessage);
}
