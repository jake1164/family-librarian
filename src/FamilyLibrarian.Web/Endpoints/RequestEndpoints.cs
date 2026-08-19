using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Web.Endpoints;

internal static class RequestEndpoints
{
    public static void MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        // Requests. Ownership is enforced inside BookRequestService, not here: these
        // routes only establish that a caller is authenticated and carries a token.
        app.MapGet("/api/v1/me/requests", ListMyRequestsAsync)
            .RequireAuthorization();

        var requests = app.MapGroup("/api/v1/requests")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        requests.MapPost("/", CreateBookRequestAsync);
        requests.MapPost("/{requestId:guid}/transitions", ChangeBookRequestStatusAsync);
    }

    private static async Task<IResult> ListMyRequestsAsync(
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        var mine = await requests.ListMineAsync(cancellationToken);

        return Results.Ok(new BookRequestListResponse(
            mine.Where(request => request.IsActive).Select(request => ToRequestResponse(request)).ToArray(),
            mine.Where(request => !request.IsActive).Select(request => ToRequestResponse(request)).ToArray()));
    }

    private static async Task<IResult> CreateBookRequestAsync(
        CreateBookRequestRequest request,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaTypes(request.Formats, out var mediaTypes))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["formats"] = ["Choose ebook, audiobook, or both."]
            });
        }

        var result = await requests.CreateAsync(
            request.WorkId,
            mediaTypes,
            request.Note,
            request.ConfirmDuplicate,
            cancellationToken);

        return result.Outcome switch
        {
            CreateBookRequestOutcome.Created => Results.Created(
                $"/api/v1/me/requests#{result.Request!.Id}",
                ToRequestResponse(result.Request)),
            // 409, not an error: the request is legitimate but the user should see
            // the outstanding one first and confirm they still want another.
            CreateBookRequestOutcome.DuplicateWarning => Results.Conflict(
                new BookRequestDuplicateResponse(
                    "You already have an outstanding request for this book.",
                    result.OverlappingFormats.Select(mediaType => mediaType.ToString()).ToArray(),
                    result.Request is null ? null : ToRequestResponse(result.Request))),
            CreateBookRequestOutcome.WorkNotFound => Results.NotFound(),
            CreateBookRequestOutcome.Unauthenticated => Results.Unauthorized(),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [result.Error ?? "That request could not be created."]
            })
        };
    }

    private static async Task<IResult> ChangeBookRequestStatusAsync(
        Guid requestId,
        ChangeBookRequestStatusRequest request,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RequestStatus>(request.Status, ignoreCase: true, out var status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["That is not a request status."]
            });
        }

        var result = await requests.TransitionAsync(
            requestId,
            status,
            request.Reason,
            request.ExpectedVersion,
            cancellationToken);

        return result.Outcome switch
        {
            BookRequestCommandOutcome.Success => Results.Ok(ToRequestResponse(result.Request!)),
            BookRequestCommandOutcome.NotFound => Results.NotFound(),
            BookRequestCommandOutcome.Unauthenticated => Results.Unauthorized(),
            BookRequestCommandOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = [result.Error ?? "That status change is not allowed."]
            })
        };
    }

    private static bool TryParseMediaTypes(
        IReadOnlyList<string>? formats,
        out RequestMediaType[] mediaTypes)
    {
        mediaTypes = [];
        if (formats is null || formats.Count is 0 or > 2)
        {
            return false;
        }

        var parsed = new List<RequestMediaType>(formats.Count);
        foreach (var format in formats)
        {
            if (!Enum.TryParse<RequestMediaType>(format, ignoreCase: true, out var mediaType) ||
                !Enum.IsDefined(mediaType))
            {
                return false;
            }

            parsed.Add(mediaType);
        }

        mediaTypes = parsed.Distinct().ToArray();
        return true;
    }

    /// <summary>
    /// Shared with <see cref="AdminRequestEndpoints"/>, which wraps this same
    /// response in the administrative view rather than duplicating its shape.
    /// </summary>
    internal static BookRequestResponse ToRequestResponse(
        BookRequestView request,
        IReadOnlyList<RequestStatus>? availableTransitions = null) => new(
        request.Id,
        request.WorkId,
        request.WorkTitle,
        request.Authors,
        request.CoverUrl,
        request.Status.ToString(),
        DescribeStatus(request.Status),
        request.IsActive,
        request.Formats
            .Select(format => new BookRequestFormatResponse(
                format.Id,
                format.MediaType.ToString(),
                format.Status.ToString(),
                format.Progress?.Code,
                format.Progress?.Description))
            .ToArray(),
        request.RequesterNote,
        request.AdminNote,
        request.RequestedAtUtc,
        request.StatusChangedAtUtc,
        (availableTransitions ?? BookRequestService.RequesterTransitionsFrom(request.Status))
            .Select(status => status.ToString())
            .ToArray(),
        request.Version);

    // Plain language for a family, not the enum name. The status itself travels
    // separately so the client never has to parse this sentence.
    private static string DescribeStatus(RequestStatus status) => status switch
    {
        RequestStatus.PendingAcquisition => "We’re checking trusted sources and preparing a safe copy when one is available.",
        RequestStatus.NeedsReview => "A librarian is reviewing this request.",
        RequestStatus.NotAvailable => "The librarian could not find this one for now.",
        RequestStatus.Cancelled => "You cancelled this request.",
        RequestStatus.Available => "Available in the family library.",
        _ => status.ToString()
    };
}
