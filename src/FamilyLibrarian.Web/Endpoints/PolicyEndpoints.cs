using FamilyLibrarian.Application.Policy;
using FamilyLibrarian.Contracts.Policy;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// The acquisition policy engine ranks the FulfillmentOptions a Work/format
/// already produces. Only the system default is admin-editable so far — see the
/// M11-part-3 plan for why a per-user override has no UI yet.
/// </summary>
internal static class PolicyEndpoints
{
    public static void MapPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var adminPolicy = app.MapGroup("/api/v1/admin/policy")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminPolicy.MapGet("/profiles", ListPolicyProfilesAsync);
        adminPolicy.MapGet("/settings", GetPolicySettingsAsync);
        adminPolicy.MapPut("/settings", SetPolicySettingsAsync);
    }

    private static IResult ListPolicyProfilesAsync(IPolicyProfileRegistry registry) =>
        Results.Ok(registry.GetProfiles()
            .Select(profile => new PolicyProfileResponse(profile.Id, profile.DisplayName, profile.Description))
            .ToArray());

    private static async Task<IResult> GetPolicySettingsAsync(
        AcquisitionPolicyService service, CancellationToken cancellationToken)
    {
        var status = await service.GetStatusAsync(cancellationToken);
        return Results.Ok(new AcquisitionPolicySettingsResponse(status.DefaultProfileId, status.UpdatedAtUtc));
    }

    private static async Task<IResult> SetPolicySettingsAsync(
        SetDefaultPolicyProfileRequest request, AcquisitionPolicyService service, CancellationToken cancellationToken)
    {
        var result = await service.SetDefaultProfileAsync(request.ProfileId, cancellationToken);
        return result.Outcome switch
        {
            AcquisitionPolicyCommandOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["profileId"] = [result.Error ?? "That change is not allowed."]
            }),
            _ => Results.Ok(new AcquisitionPolicySettingsResponse(result.Status!.DefaultProfileId, result.Status.UpdatedAtUtc))
        };
    }
}
