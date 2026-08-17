using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Contracts.Feedback;
using Microsoft.AspNetCore.Mvc;

namespace FamilyLibrarian.Web.Endpoints;

internal static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        // My Reading: a completion date and 1-5 star rating per Work, private to the
        // owner. Ownership is enforced inside UserWorkFeedbackService, same as Requests.
        var feedback = app.MapGroup("/api/v1/me/feedback")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        feedback.MapGet("/", ListMyFeedbackAsync);
        feedback.MapGet("/{workId:guid}", GetMyFeedbackAsync);
        feedback.MapPut("/{workId:guid}", SetMyFeedbackAsync);
        feedback.MapDelete("/{workId:guid}", RemoveMyFeedbackAsync);
    }

    private static async Task<IResult> ListMyFeedbackAsync(
        UserWorkFeedbackService feedback,
        CancellationToken cancellationToken)
    {
        var mine = await feedback.ListMineAsync(cancellationToken);
        return Results.Ok(new WorkFeedbackListResponse(mine.Select(ToFeedbackResponse).ToArray()));
    }

    private static async Task<IResult> GetMyFeedbackAsync(
        Guid workId,
        UserWorkFeedbackService feedback,
        CancellationToken cancellationToken)
    {
        var mine = await feedback.FindMineAsync(workId, cancellationToken);
        return mine is null ? Results.NotFound() : Results.Ok(ToFeedbackResponse(mine));
    }

    private static async Task<IResult> SetMyFeedbackAsync(
        Guid workId,
        SetWorkFeedbackRequest request,
        UserWorkFeedbackService feedback,
        CancellationToken cancellationToken)
    {
        var result = await feedback.SetFeedbackAsync(
            workId,
            request.CompletedOn,
            request.Rating,
            request.ExpectedVersion,
            cancellationToken);

        return result.Outcome switch
        {
            SetFeedbackOutcome.Success => Results.Ok(ToFeedbackResponse(result.Feedback!)),
            SetFeedbackOutcome.WorkNotFound => Results.NotFound(),
            SetFeedbackOutcome.Unauthenticated => Results.Unauthorized(),
            SetFeedbackOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["rating"] = [result.Error ?? "That rating could not be saved."]
            })
        };
    }

    private static async Task<IResult> RemoveMyFeedbackAsync(
        Guid workId,
        [FromBody] RemoveWorkFeedbackRequest request,
        UserWorkFeedbackService feedback,
        CancellationToken cancellationToken)
    {
        var result = await feedback.RemoveFeedbackAsync(workId, request.ExpectedVersion, cancellationToken);

        return result.Outcome switch
        {
            RemoveFeedbackOutcome.Success => Results.NoContent(),
            RemoveFeedbackOutcome.NotFound => Results.NotFound(),
            RemoveFeedbackOutcome.Unauthenticated => Results.Unauthorized(),
            RemoveFeedbackOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static WorkFeedbackResponse ToFeedbackResponse(UserWorkFeedbackView feedback) => new(
        feedback.WorkId,
        feedback.WorkTitle,
        feedback.Authors,
        feedback.CoverUrl,
        feedback.CompletedOn,
        feedback.Rating,
        feedback.Version);
}
