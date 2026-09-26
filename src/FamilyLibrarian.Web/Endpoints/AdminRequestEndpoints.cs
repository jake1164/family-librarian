using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
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
        adminRequests.MapGet("/attention", GetAttentionAsync);
        adminRequests.MapGet("/active-acquisitions", GetActiveAcquisitions);
        adminRequests.MapPost("/recheck", RecheckNeedsReviewAsync);
        adminRequests.MapGet("/provider-interactions", ListProviderInteractionsAsync);
        adminRequests.MapPost("/provider-interactions/{jobId:guid}/start", StartProviderInteractionAsync);
        adminRequests.MapPost("/provider-interactions/{jobId:guid}/fallback", UseProviderInteractionFallbackAsync);
        adminRequests.MapPost("/provider-interactions/{jobId:guid}/cancel", CancelProviderInteractionAsync);
        adminRequests.MapPost("/provider-interactions/{jobId:guid}/take-over", TakeOverProviderInteractionAsync);
        adminRequests.MapGet("/provider-interactions/{jobId:guid}/view", HandleProviderInteractionViewAsync);
        adminRequests.MapGet("/{requestId:guid}", GetAdminRequestAsync);
        adminRequests.MapGet("/{requestId:guid}/provider-interaction", GetProviderInteractionForRequestAsync);
        adminRequests.MapGet("/{requestId:guid}/provider-attempts", ListProviderAttemptsAsync);
        adminRequests.MapPost("/{requestId:guid}/transitions", ChangeAdminRequestStatusAsync);
        adminRequests.MapPost("/{requestId:guid}/needs-review/resolve", AdminResolveNeedsReviewAsync);
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

    private static IResult GetActiveAcquisitions(ActiveAcquisitionTracker tracker, IProviderRegistry registry)
    {
        var now = DateTimeOffset.UtcNow;
        return Results.Ok(tracker.Snapshot().Select(activity => new AdminActiveAcquisitionResponse(
            activity.RequestId,
            activity.RequestFormatId,
            activity.ProviderId,
            registry.Find(activity.ProviderId)?.DisplayName ?? activity.ProviderId,
            activity.Stage,
            activity.WorkTitle,
            activity.BytesReceived,
            activity.TotalBytes,
            activity.Stage == "Downloading" && activity.TransferStartedAtUtc is { } startedAt && now > startedAt
                ? (long)(Math.Max(0, activity.BytesReceived - activity.TransferStartBytesReceived) / (now - startedAt).TotalSeconds)
                : null,
            activity.StartedAtUtc)).ToArray());
    }

    private static async Task<IResult> GetAdminRequestAsync(
        Guid requestId,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        var request = await requests.GetForAdminAsync(requestId, cancellationToken);
        return request is null ? Results.NotFound() : Results.Ok(ToAdminRequestResponse(request));
    }

    private static async Task<IResult> ListProviderInteractionsAsync(
        ProviderInteractionService service,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        Results.Ok((await service.ListAsync(currentUser.UserId, cancellationToken)).Select(ToProviderInteractionResponse).ToArray());

    private static async Task<IResult> GetProviderInteractionForRequestAsync(
        Guid requestId,
        ProviderInteractionService service,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        // 200/null, not 404: "no interaction waiting" is this request's
        // ordinary state, not an exceptional one -- the client deserializes
        // straight to null without a caught-exception round trip. Since
        // .NET 7/8, Results.Ok(null) writes zero bytes rather than the JSON
        // literal "null" (https://github.com/dotnet/aspnetcore/issues/53509),
        // which GetFromJsonAsync<T> on the Blazor WASM client cannot parse
        // (JsonException: ExpectedJsonTokens) -- write the literal ourselves.
        var interaction = await service.FindForRequestAsync(requestId, currentUser.UserId, cancellationToken);
        return interaction is null
            ? Results.Text("null", "application/json")
            : Results.Ok(ToProviderInteractionResponse(interaction));
    }

    private static ProviderInteractionResponse ToProviderInteractionResponse(ProviderInteractionView interaction) => new(
        interaction.ProviderAcquisitionJobId,
        interaction.RequestId,
        interaction.RequestFormatId,
        interaction.ProviderId,
        interaction.Type,
        interaction.Message,
        interaction.ExpiresAtUtc,
        interaction.ResumeSupported,
        interaction.IsExpired,
        interaction.CanStart,
        interaction.CanUseFallback,
        interaction.CanCancel,
        interaction.CanViewNow,
        interaction.WorkTitle,
        interaction.Authors,
        interaction.RequesterDisplayName,
        interaction.ClaimedByDisplayName,
        interaction.IsClaimedByCurrentUser);

    private static async Task<IResult> StartProviderInteractionAsync(
        Guid jobId,
        ProviderInteractionService service,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await ToProviderInteractionResult(service.StartAsync(jobId, userId, cancellationToken));
    }

    private static Task<IResult> UseProviderInteractionFallbackAsync(
        Guid jobId,
        ProviderInteractionService service,
        CancellationToken cancellationToken) =>
        ToProviderInteractionResult(service.UseFallbackAsync(jobId, cancellationToken));

    private static Task<IResult> CancelProviderInteractionAsync(
        Guid jobId,
        ProviderInteractionService service,
        CancellationToken cancellationToken) =>
        ToProviderInteractionResult(service.CancelAsync(jobId, cancellationToken));

    /// <summary>Explicit, audited override: replaces whoever currently holds the claim, then starts the session.</summary>
    private static async Task<IResult> TakeOverProviderInteractionAsync(
        Guid jobId,
        ProviderInteractionClaimService claimService,
        ProviderInteractionService service,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        try
        {
            await claimService.TakeOverAsync(jobId, userId, cancellationToken);
        }
        catch (TimeoutException)
        {
            return Results.Conflict(new { message = "The current view is still closing. Try taking over again." });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { message = "This job is no longer waiting for interaction." });
        }
        return await ToProviderInteractionResult(service.StartAsync(jobId, userId, cancellationToken));
    }

    private static async Task<IResult> ToProviderInteractionResult(Task<ProviderInteractionCommandOutcome> operation)
    {
        var outcome = await operation;
        return outcome.Result switch
        {
            ProviderInteractionCommandResult.Success => Results.NoContent(),
            ProviderInteractionCommandResult.NotFound => Results.NotFound(),
            ProviderInteractionCommandResult.NotWaiting => Results.Conflict(new
            {
                message = "This provider acquisition is no longer waiting for human interaction. Reload the queue."
            }),
            ProviderInteractionCommandResult.Expired => Results.Conflict(new
            {
                message = "This provider interaction has expired. Reload the queue before choosing a new action."
            }),
            ProviderInteractionCommandResult.Unsupported => Results.Conflict(new
            {
                message = "This provider does not support administrator interaction control."
            }),
            ProviderInteractionCommandResult.ClaimedByAnother => Results.Conflict(new
            {
                message = $"Being handled by {outcome.ClaimedByDisplayName}. Use Take over if they're stuck.",
                claimedBy = outcome.ClaimedByDisplayName
            }),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }

    /// <summary>
    /// The brokered remote-view WebSocket (HUMAN-ACQ-1 Phase 3, docs/04 §8
    /// "Optional interaction view"). Eligibility is checked <em>before</em>
    /// accepting the upgrade so an ineligible request gets a plain HTTP
    /// status, not an upgrade immediately followed by a close.
    /// </summary>
    private static async Task<IResult> HandleProviderInteractionViewAsync(
        Guid jobId,
        HttpContext context,
        ProviderRemoteViewBrokerService broker,
        ProviderInteractionClaimService claimService,
        ICurrentUser currentUser,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new { message = "This route only accepts a WebSocket upgrade." });
        }

        // Opening the remote view also counts as a claim (HUMAN-ACQ-1 D7) --
        // checked, and rejected with a plain HTTP status, before the WebSocket
        // upgrade so a busy admin never sees an upgrade immediately closed.
        if (currentUser.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        var claim = await claimService.TryClaimForUserAsync(
            jobId, userId, ProviderInteractionClaimChannel.InApp, alertId: null, cancellationToken);
        if (claim.Kind == ClaimOutcomeKind.HeldByOther)
        {
            return Results.Conflict(new
            {
                message = $"Being handled by {claim.HeldByDisplayName}. Use Take over if they're stuck.",
                claimedBy = claim.HeldByDisplayName
            });
        }

        return await RemoteViewSocketHandler.HandleAsync(jobId, context, broker, lifetime, cancellationToken);
    }

    /// <summary>
    /// SELFSERV-1: admin counterpart of the requester's own needs-review
    /// resolve route -- additive, not exclusive. No ownership check.
    /// </summary>
    private static async Task<IResult> AdminResolveNeedsReviewAsync(
        Guid requestId,
        ResolveNeedsReviewRequest request,
        AutomaticRequestFulfillmentService fulfillment,
        BookRequestService requests,
        CancellationToken cancellationToken)
    {
        var outcome = request.CandidateId is { } candidateId
            ? await fulfillment.AdminResolvePreferenceAmbiguityAsync(requestId, candidateId, request.ExpectedVersion, cancellationToken)
            : await fulfillment.AdminDismissPreferenceAmbiguityAsync(requestId, request.ExpectedVersion, cancellationToken);

        if (outcome == PreferenceAmbiguityResolutionOutcome.NotFound)
        {
            return Results.NotFound();
        }

        if (outcome == PreferenceAmbiguityResolutionOutcome.Conflict)
        {
            return Results.Conflict(new { message = "Someone else updated this request. Reload it before making another change." });
        }

        var view = await requests.GetForAdminAsync(requestId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(ToAdminRequestResponse(view));
    }

    private static async Task<IResult> RecheckNeedsReviewAsync(
        RecheckNeedsReviewRequest request,
        BookRequestService requests,
        IEnumerable<IAutomaticDirectAcquisitionProvider> automaticProviders,
        CancellationToken cancellationToken)
    {
        string? providerId = string.IsNullOrWhiteSpace(request.ProviderId) ? null : request.ProviderId.Trim();
        if (providerId is not null &&
            !automaticProviders.Any(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["providerId"] = ["That provider is not registered for automatic acquisition."]
            });
        }

        var result = await requests.AdminBulkRecheckAsync(providerId, cancellationToken);
        return result.Outcome switch
        {
            BulkRecheckOutcome.Success => Results.Ok(new RecheckNeedsReviewResponse(result.RequeuedCount)),
            _ => Results.Unauthorized()
        };
    }

    private static async Task<IResult> GetAttentionAsync(
        IRequestRepository requests,
        IProviderAttemptRepository attempts,
        IProviderRegistry registry,
        IExternalProviderStore externalProviders,
        ProviderInteractionService providerInteractions,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        // All stores here are scoped over the same AppDbContext. EF Core does
        // not allow concurrent operations on that context, so keep these small
        // administrative projections sequential rather than fanning them out.
        var needsReviewCount = await requests.CountForAdminAsync(RequestStatus.NeedsReview, cancellationToken);
        var latestAttempts = await attempts.ListLatestByProviderAsync(cancellationToken);
        var registeredExternalProviders = await externalProviders.ListAsync(cancellationToken);
        var waitingInteractions = await providerInteractions.ListAsync(currentUser.UserId, cancellationToken);

        var displayNames = registry.GetInstalledProviders()
            .ToDictionary(provider => provider.Id, provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var provider in registeredExternalProviders)
        {
            displayNames[provider.ProviderId] = provider.DisplayName;
        }

        var providerIssues = latestAttempts
            .Where(attempt => attempt.IssueKind is not null)
            // Historic provider activity stays available on the request's
            // timeline, but only currently installed/registered providers can
            // be an active source-health issue.
            .Where(attempt => displayNames.ContainsKey(attempt.ProviderId))
            .OrderByDescending(attempt => attempt.AttemptedAtUtc)
            .Select(attempt => new AdminProviderIssueResponse(
                attempt.ProviderId,
                displayNames.GetValueOrDefault(attempt.ProviderId, attempt.ProviderId),
                attempt.Summary,
                attempt.AttemptedAtUtc,
                attempt.IssueKind!.Value.ToString()))
            .ToArray();

        var providerInteractionsWaiting = waitingInteractions
            .Select(interaction => new AdminProviderInteractionAttentionResponse(
                interaction.ProviderAcquisitionJobId,
                interaction.RequestId,
                interaction.RequestFormatId,
                interaction.WorkTitle,
                interaction.ProviderId,
                interaction.Type,
                interaction.Message,
                interaction.ExpiresAtUtc,
                interaction.IsExpired,
                interaction.ClaimedByDisplayName,
                interaction.IsClaimedByCurrentUser))
            .ToArray();

        return Results.Ok(new AdminRequestAttentionResponse(needsReviewCount, providerIssues, providerInteractionsWaiting));
    }

    private static async Task<IResult> ListProviderAttemptsAsync(
        Guid requestId,
        IRequestRepository requests,
        IProviderAttemptRepository attempts,
        CancellationToken cancellationToken)
    {
        if (await requests.FindRequestForAdminAsync(requestId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        return Results.Ok((await attempts.ListForRequestAsync(requestId, cancellationToken))
            .Select(attempt => new ProviderAttemptResponse(
                attempt.Id,
                attempt.RequestFormatId,
                attempt.ProviderId,
                attempt.Outcome.ToString(),
                attempt.Summary,
                attempt.AttemptedAtUtc,
                attempt.NextEligibleCheckAtUtc))
            .ToArray());
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
        AutomatedSecurityPipeline securityPipeline,
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

                // Every successful upload is evaluated immediately. A clean,
                // valid result is trusted and dispatched by the policy; only
                // failed or inconclusive evaluations need follow-up.
                if (result.Outcome == ManualImportOutcome.Success)
                {
                    await securityPipeline.EvaluateAsync(result.MediaAssetId!.Value, cancellationToken);
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
        DirectAcquisitionSecurityService acquisitions,
        IProviderAttemptRepository providerAttempts,
        IClock clock,
        CancellationToken cancellationToken,
        bool confirmLowConfidenceMatch = false)
    {
        var result = await acquisitions.AcquireAndEvaluateAsync(
            requestId, formatId, providerId, providerResultId, cancellationToken, confirmLowConfidenceMatch);

        // A librarian's manual "get free copy" click is still a provider
        // lookup; record it in the same ledger the automatic poller uses so
        // "Provider activity" and the Tasks dashboard's "Source and download
        // activity" don't go stale the moment an admin works around an
        // automatic failure by retrying manually — see ProviderAttempt.
        var attemptOutcome = result.Outcome switch
        {
            ManualImportOutcome.Success => ProviderAttemptOutcome.Acquired,
            ManualImportOutcome.AcquisitionInProgress => ProviderAttemptOutcome.Submitted,
            _ => ProviderAttemptOutcome.Failed
        };
        var attemptSummary = result.Outcome switch
        {
            ManualImportOutcome.Success => "A copy was manually fetched by a librarian and sent through the security pipeline.",
            ManualImportOutcome.AcquisitionInProgress => "A librarian started an acquisition; it is being tracked to completion.",
            _ => result.Error ?? "The manual fetch could not be completed."
        };
        providerAttempts.Add(new ProviderAttempt(
            requestId,
            formatId,
            providerId,
            attemptOutcome,
            attemptSummary,
            clock.UtcNow,
            nextEligibleCheckAtUtc: result.Outcome == ManualImportOutcome.TransferInterrupted
                ? clock.UtcNow.AddMinutes(2)
                : null));
        await providerAttempts.SaveChangesAsync(cancellationToken);

        return ToManualImportResult(result);
    }

    private static IResult ToManualImportResult(ManualImportResult result) => result.Outcome switch
    {
        ManualImportOutcome.Success => Results.Ok(
            new ManualImportResultResponse(result.AcquisitionJobId!.Value, result.MediaAssetId!.Value)),
        ManualImportOutcome.AcquisitionInProgress => Results.Accepted(
            value: new ManualAcquisitionInProgressResponse(result.ProviderAcquisitionJobId!.Value)),
        ManualImportOutcome.DuplicateDetected => Results.Conflict(new { message = result.Error }),
        ManualImportOutcome.LowConfidenceMatchConfirmationRequired => Results.Conflict(
            new { message = result.Error, requiresConfirmation = true }),
        ManualImportOutcome.ReleaseConfirmationRequired => Results.Conflict(
            new { message = result.Error, requiresConfirmation = true }),
        ManualImportOutcome.WaitingForSecurityScanner => Results.Problem(
            detail: result.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "WAITING_FOR_SECURITY_SCANNER"),
        ManualImportOutcome.TransferInterrupted => Results.Problem(
            detail: result.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "ACQUISITION_TRANSFER_INTERRUPTED"),
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
            BookRequestService.AdminTransitionsFrom(request.Request.Status)
                .Where(status => !request.Request.RequiresManualFulfillment || status != RequestStatus.PendingAcquisition).ToArray()),
        request.RequesterDisplayName,
        request.RequesterEmail,
        request.StatusHistory
            .Select(history => new BookRequestStatusHistoryResponse(
                history.FromStatus?.ToString(),
                history.ToStatus.ToString(),
                history.Reason,
                history.OccurredAtUtc))
            .ToArray(),
        request.Participants?.Select(participant => new RequestParticipantResponse(
            participant.DisplayName, participant.Email, participant.Note, participant.Withdrawn)).ToArray(),
        request.ReviewCandidates?.Select(candidate => new AdminRequestReviewCandidateResponse(
            candidate.CandidateId,
            candidate.RequestFormatId,
            candidate.ProviderId,
            candidate.ProviderResultId,
            candidate.Title,
            candidate.Author,
            candidate.Language,
            candidate.Details,
            ResolveInspectionUri(candidate))).ToArray());

    private static string? ResolveInspectionUri(AdminRequestReviewCandidateView candidate)
{
    if (!string.IsNullOrWhiteSpace(candidate.InspectionUri))
    {
        return candidate.InspectionUri;
    }

    // Older built-in-provider reviews predate AdminInspectionUri. Their
    // positive catalogue IDs are still sufficient to point an administrator at
    // the same public record; no opaque external-provider result is inferred.
    return candidate.ProviderId.Equals("gutendex", StringComparison.OrdinalIgnoreCase) &&
           int.TryParse(candidate.ProviderResultId, out var gutenbergId) && gutenbergId > 0
        ? $"https://www.gutenberg.org/ebooks/{gutenbergId}"
        : null;
    }
}
