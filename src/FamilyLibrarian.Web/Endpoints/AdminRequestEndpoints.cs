using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Requests;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace FamilyLibrarian.Web.Endpoints;

internal static class AdminRequestEndpoints
{
    public static void MapAdminRequestEndpoints(this IEndpointRouteBuilder app)
    {
        // Request review is an administrative surface, distinct from the requester's
        // own routes. The service enforces the state matrix; this group supplies the
        // role check and anti-forgery protection for cookie-authenticated mutations.
        var adminRequests = app.MapGroup("/api/v1/admin/requests")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminRequests.MapGet("/", ListAdminRequestsAsync);
        adminRequests.MapGet("/{requestId:guid}", GetAdminRequestAsync);
        adminRequests.MapPost("/{requestId:guid}/transitions", ChangeAdminRequestStatusAsync);
        adminRequests.MapPut("/{requestId:guid}/note", SetAdminRequestNoteAsync);
        adminRequests.MapPost("/{requestId:guid}/formats/{formatId:guid}/manual-import", ManualImportAsync);
        adminRequests.MapPost(
            "/{requestId:guid}/formats/{formatId:guid}/direct-acquisitions/{providerId}/{providerResultId}",
            AcquireDirectAsync);
    }

    private static async Task<IResult> ListAdminRequestsAsync(
        string? status,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        RequestStatus? requestedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<RequestStatus>(status, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["That is not a request status."]
                });
            }

            requestedStatus = parsed;
        }

        var queue = await requests.ListForAdminAsync(requestedStatus, cancellationToken);
        return Results.Ok(new AdminBookRequestListResponse(
            queue.Select(ToAdminRequestResponse).ToArray()));
    }

    private static async Task<IResult> GetAdminRequestAsync(
        Guid requestId,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        var request = await requests.GetForAdminAsync(requestId, cancellationToken);
        return request is null ? Results.NotFound() : Results.Ok(ToAdminRequestResponse(request));
    }

    private static async Task<IResult> ChangeAdminRequestStatusAsync(
        Guid requestId,
        ChangeBookRequestStatusRequest request,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RequestStatus>(request.Status, ignoreCase: true, out var status) ||
            !Enum.IsDefined(status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["That is not a request status."]
            });
        }

        if (request.ExpectedVersion is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expectedVersion"] = ["Reload the request before changing its status."]
            });
        }

        var result = await requests.AdminTransitionAsync(
            requestId,
            status,
            request.Reason,
            request.ExpectedVersion.Value,
            cancellationToken);

        return await ToAdminCommandResult(result, requestId, requests, cancellationToken);
    }

    private static async Task<IResult> SetAdminRequestNoteAsync(
        Guid requestId,
        SetAdminBookRequestNoteRequest request,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        var result = await requests.SetAdminNoteAsync(
            requestId,
            request.Note,
            request.ExpectedVersion,
            cancellationToken);

        return await ToAdminCommandResult(result, requestId, requests, cancellationToken);
    }

    private static async Task<IResult> ManualImportAsync(
        Guid requestId,
        Guid formatId,
        HttpRequest request,
        ManualImportService manualImport,
        SecurityEvaluationService securityEvaluation,
        ManualImportPolicy policy,
        CancellationToken cancellationToken)
    {
        // Deliberately not bound as [FromForm]/IFormFile: minimal-API model binding
        // for those runs before any endpoint filter or handler code executes, which
        // would already have buffered the whole body to a temp file before this
        // method could enforce a size limit. Reading the multipart body by hand lets
        // the size cap below apply before a single byte is accepted.
        if (!request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = policy.MaxUploadSizeBytes;
        }

        var boundary = Microsoft.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType).Boundary.Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return Results.BadRequest();
        }

        var reader = new MultipartReader(boundary, request.Body);
        for (var section = await reader.ReadNextSectionAsync(cancellationToken);
            section is not null;
            section = await reader.ReadNextSectionAsync(cancellationToken))
        {
            var fileSection = section.AsFileSection();
            if (fileSection is null)
            {
                continue;
            }

            var fileName = fileSection.FileName;
            if (string.IsNullOrEmpty(fileName))
            {
                return Results.BadRequest();
            }

            ManualImportResult result;
            try
            {
                result = await manualImport.ImportAsync(
                    requestId,
                    formatId,
                    fileSection.FileStream ?? section.Body,
                    fileName,
                    cancellationToken);

                // A successful upload is evaluated immediately. The resulting
                // asset still needs an explicit approval before it is trusted,
                // but administrators should not need to start a routine scan.
                if (result.Outcome == ManualImportOutcome.Success)
                {
                    await securityEvaluation.EvaluateAsync(result.MediaAssetId!.Value, cancellationToken);
                }
            }
            catch (BadHttpRequestException)
            {
                // The body exceeded the size limit set above.
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            return ToManualImportResult(result);
        }

        // No file section was present in the form.
        return Results.BadRequest();
    }

    private static async Task<IResult> AcquireDirectAsync(
        Guid requestId,
        Guid formatId,
        string providerId,
        string providerResultId,
        DirectAcquisitionService acquisitions,
        CancellationToken cancellationToken)
    {
        var result = await acquisitions.AcquireAsync(
            requestId, formatId, providerId, providerResultId, cancellationToken);
        return ToManualImportResult(result);
    }

    private static IResult ToManualImportResult(ManualImportResult result) => result.Outcome switch
    {
        ManualImportOutcome.Success => Results.Ok(
            new ManualImportResultResponse(result.AcquisitionJobId!.Value, result.MediaAssetId!.Value)),
        ManualImportOutcome.DuplicateDetected => Results.Conflict(new { message = result.Error }),
        ManualImportOutcome.WaitingForSecurityScanner => Results.Problem(
            detail: result.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "WAITING_FOR_SECURITY_SCANNER"),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["file"] = [result.Error ?? "That file could not be imported."]
        })
    };

    private static async Task<IResult> ToAdminCommandResult(
        BookRequestCommandResult result,
        Guid requestId,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        if (result.Outcome == BookRequestCommandOutcome.Success)
        {
            var updated = await requests.GetForAdminAsync(requestId, cancellationToken);
            return Results.Ok(ToAdminRequestResponse(updated!));
        }

        return result.Outcome switch
        {
            BookRequestCommandOutcome.NotFound => Results.NotFound(),
            BookRequestCommandOutcome.Unauthenticated => Results.Unauthorized(),
            BookRequestCommandOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [result.Error ?? "That request could not be updated."]
            })
        };
    }

    private static AdminBookRequestResponse ToAdminRequestResponse(AdminBookRequestView request) => new(
        RequestEndpoints.ToRequestResponse(
            request.Request,
            BookRequestService.AdminTransitionsFrom(request.Request.Status)),
        request.RequesterDisplayName,
        request.RequesterEmail,
        request.StatusHistory
            .Select(history => new BookRequestStatusHistoryResponse(
                history.FromStatus?.ToString(),
                history.ToStatus.ToString(),
                history.Reason,
                history.OccurredAtUtc))
            .ToArray());
}
