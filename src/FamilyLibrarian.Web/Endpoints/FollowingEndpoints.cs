using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Contracts.Following;
using FamilyLibrarian.Domain.Following;

namespace FamilyLibrarian.Web.Endpoints;

internal static class FollowingEndpoints
{
    public static void MapFollowingEndpoints(this IEndpointRouteBuilder app)
    {
        // Following: a per-user subscription to a Series or an Author, private
        // to the owner. Ownership is enforced inside FollowService, same as
        // Requests and Feedback.
        var following = app.MapGroup("/api/v1/me/follows")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        following.MapGet("/", GetMyFollowingAsync);
        following.MapPost("/series/{seriesId:guid}", FollowSeriesAsync);
        following.MapDelete("/series/{seriesId:guid}", UnfollowSeriesAsync);
        following.MapPost("/authors/{authorId:guid}", FollowAuthorAsync);
        following.MapDelete("/authors/{authorId:guid}", UnfollowAuthorAsync);
    }

    private static async Task<IResult> GetMyFollowingAsync(
        FollowService follows,
        CancellationToken cancellationToken)
    {
        var series = await follows.ListMyFollowedSeriesAsync(cancellationToken);
        var authors = await follows.ListMyFollowedAuthorsAsync(cancellationToken);

        return Results.Ok(new FollowingSummaryResponse(
            series.Select(ToResponse).ToArray(),
            authors.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> FollowSeriesAsync(
        Guid seriesId,
        FollowService follows,
        CancellationToken cancellationToken)
    {
        var result = await follows.FollowSeriesAsync(seriesId, cancellationToken);
        return ToFollowResult(result);
    }

    private static async Task<IResult> UnfollowSeriesAsync(
        Guid seriesId,
        FollowService follows,
        CancellationToken cancellationToken)
    {
        var removed = await follows.UnfollowAsync(
            FollowSubjectType.Series, seriesId, cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> FollowAuthorAsync(
        Guid authorId,
        FollowService follows,
        CancellationToken cancellationToken)
    {
        var result = await follows.FollowAuthorAsync(authorId, cancellationToken);
        return ToFollowResult(result);
    }

    private static async Task<IResult> UnfollowAuthorAsync(
        Guid authorId,
        FollowService follows,
        CancellationToken cancellationToken)
    {
        var removed = await follows.UnfollowAsync(
            FollowSubjectType.Author, authorId, cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static IResult ToFollowResult(FollowResult result) => result.Outcome switch
    {
        FollowOutcome.Success => Results.Ok(new { id = result.FollowId }),
        FollowOutcome.NotFound => Results.NotFound(),
        FollowOutcome.Unauthenticated => Results.Unauthorized(),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private static FollowedSeriesResponse ToResponse(FollowedSeriesView view) => new(
        view.FollowId,
        view.SeriesId,
        view.SeriesName,
        view.Status.ToString(),
        view.Entries.Select(entry => new FollowedSeriesEntryResponse(
            entry.WorkId,
            entry.WorkTitle,
            entry.PositionLabel,
            entry.IsPrimary,
            entry.IsRead,
            entry.IsOwned)).ToArray(),
        view.IsCaughtUp);

    private static FollowedAuthorResponse ToResponse(FollowedAuthorView view) => new(
        view.FollowId,
        view.AuthorId,
        view.AuthorName,
        view.Works.Select(work => new FollowedAuthorWorkResponse(
            work.WorkId,
            work.WorkTitle,
            work.IsRead,
            work.IsOwned)).ToArray());
}
