using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>A household member's own audiobook narration preference -- self-service, not admin-authorized.</summary>
internal static class AudiobookNarrationPreferenceEndpoints
{
    public static void MapAudiobookNarrationPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var preference = app.MapGroup("/api/v1/me/audiobook-narration-preference")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        preference.MapGet("/", GetAsync);
        preference.MapPut("/", SetAsync);
    }

    private static async Task<IResult> GetAsync(
        AudiobookNarrationPreferenceService service, CancellationToken cancellationToken)
    {
        var current = await service.GetAsync(cancellationToken);
        return current is null
            ? Results.Unauthorized()
            : Results.Ok(new AudiobookNarrationPreferenceResponse(current.Value.ToString()));
    }

    private static async Task<IResult> SetAsync(
        SetAudiobookNarrationPreferenceRequest request,
        AudiobookNarrationPreferenceService service,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AudiobookNarrationPreference>(request.Preference, ignoreCase: true, out var preference))
        {
            return Invalid("That is not an audiobook narration preference.");
        }

        var result = await service.SetAsync(preference, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new AudiobookNarrationPreferenceResponse(preference.ToString()))
            : Invalid(result.Error!);
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["preference"] = [message] });
}
