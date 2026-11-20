using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Explicit source lookups under <c>/api/v1/catalog/discovery</c>. The two GET routes read only the user's own temporary
/// contexts and the provider settings: they never contact a metadata provider. The two POST routes are the only place a lookup
/// can reach a provider, and each is authenticated and anti-forgery protected before anything else happens. A request names a
/// server-issued context and subject (or a window to continue); it can never carry a native id, an owner, a URL or a budget.
/// </summary>
internal static class CatalogDiscoveryEndpoints
{
    public static void MapCatalogDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/catalog/discovery")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapGet("/contexts/{contextId:guid}/subjects/{subjectId:guid}/offers", GetOffersAsync);
        group.MapGet("/windows/{contextId:guid}", GetWindow);
        group.MapPost("/expansions", ExpandAsync);
        group.MapPost("/expansions/continue", ContinueAsync);
    }

    private static async Task<IResult> GetOffersAsync(
        Guid contextId, Guid subjectId, ICurrentUser currentUser, CatalogDiscoveryService service, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();
        var offers = await service.OffersAsync(ownerId, contextId, subjectId, cancellationToken);
        return offers.Status != ExpansionStatus.Ok
            ? Results.NotFound()
            : Results.Ok(new CatalogExpansionOffersResponse(offers.Offers.Select(offer => new CatalogExpansionOfferResponse(
                offer.Kind.ToString(), offer.ProviderId, offer.ProviderName, offer.Capability, offer.ReasonCode, offer.CanStart)).ToArray()));
    }

    private static IResult GetWindow(
        Guid contextId, ICurrentUser currentUser, IClock clock, CatalogBrowseContextStore store)
    {
        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();
        // Unknown, expired, evicted, another owner's and not-a-window all answer the same 404.
        return store.TryGetWindow(ownerId, contextId, clock.UtcNow) is { } window
            ? Results.Ok(ToResponse(window))
            : Results.NotFound();
    }

    private static async Task<IResult> ExpandAsync(
        CatalogExpansionRequest? request, ICurrentUser currentUser, CatalogDiscoveryService service, CancellationToken cancellationToken)
    {
        if (request is null || request.ContextId == Guid.Empty || request.SubjectId == Guid.Empty ||
            !Enum.TryParse<ExpansionKind>(request.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Choose a valid lookup."] });
        }

        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();
        return ToHttp(await service.ExpandAsync(ownerId, request.ContextId, request.SubjectId, kind, cancellationToken));
    }

    private static async Task<IResult> ContinueAsync(
        CatalogExpansionContinueRequest? request, ICurrentUser currentUser, CatalogDiscoveryService service, CancellationToken cancellationToken)
    {
        if (request is null || request.ContextId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["contextId"] = ["Choose a result to continue."] });
        }

        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();
        return ToHttp(await service.ContinueAsync(ownerId, request.ContextId, cancellationToken));
    }

    private static IResult ToHttp(ExpansionResult result)
    {
        switch (result.Status)
        {
            case ExpansionStatus.Ok:
                return result.Window is { } window
                    ? Results.Ok(ToResponse(window))
                    : Results.Ok(ToResponse(result.Header!));
            case ExpansionStatus.NotFound:
                return Results.NotFound();
            case ExpansionStatus.BadScope:
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["That lookup does not apply here."] });
            case ExpansionStatus.RateLimited:
                return Refusal(StatusCodes.Status429TooManyRequests, "rate-limited", result.RetryAfter);
            case ExpansionStatus.Busy:
                return Refusal(StatusCodes.Status429TooManyRequests, "lookup-running", TimeSpan.Zero);
            case ExpansionStatus.SourceUnavailable:
                return Refusal(StatusCodes.Status409Conflict, result.Code ?? "source-unavailable", TimeSpan.Zero);
            case ExpansionStatus.Unsupported:
                return Refusal(StatusCodes.Status409Conflict, result.Code ?? "unsupported", TimeSpan.Zero);
            case ExpansionStatus.JourneyEnded:
                return Refusal(StatusCodes.Status409Conflict, "journey-ended", TimeSpan.Zero);
            default:
                return Refusal(StatusCodes.Status409Conflict, "no-continuation", TimeSpan.Zero);
        }
    }

    private static RefusalResult Refusal(int status, string code, TimeSpan retryAfter) =>
        new RefusalResult(status, new CatalogExpansionProblemResponse(code), retryAfter);

    private sealed class RefusalResult(int status, CatalogExpansionProblemResponse body, TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (retryAfter > TimeSpan.Zero)
            {
                httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return Results.Json(body, statusCode: status).ExecuteAsync(httpContext);
        }
    }

    // ---- mapping: stable codes and counts only ----

    private static CatalogDiscoveryResponse ToResponse(DiscoveryWindowView window)
    {
        var header = window.Header;
        var limitations = new List<string> { CatalogBrowseLimitations.SourceLookup };
        if (window.Truncated || header.ItemsRetained < header.ItemsOffered) limitations.Add(CatalogBrowseLimitations.Truncated);
        return Map(header, window.ContextId, window.ExpiresAtUtc, window.Records.Select(ToResponse).ToArray(), window.Refs.Select(ToResponse).ToArray(), limitations);
    }

    private static CatalogDiscoveryResponse ToResponse(ObservedDiscovery header) =>
        Map(header, null, null, [], [], [CatalogBrowseLimitations.SourceLookup, CatalogBrowseLimitations.NavigationUnavailable]);

    private static CatalogDiscoveryResponse Map(
        ObservedDiscovery header,
        Guid? contextId,
        DateTimeOffset? expiresAtUtc,
        IReadOnlyList<CatalogDiscoveryBookResponse> books,
        IReadOnlyList<CatalogDiscoveryRefResponse> refs,
        IReadOnlyList<string> limitations) => new(
            contextId, header.ParentContextId, header.ParentSubjectId, header.Operation, header.ProviderId, header.ProviderName,
            header.SubjectKind.ToString(), header.SubjectLabel, header.Outcome, header.Completion, header.Notes, header.CapabilityState,
            header.ScopeFilter, header.ObservedAtUtc, expiresAtUtc, header.Attempts, header.Retries, header.Pages,
            header.ItemsOffered, header.ItemsRetained, header.ContinuationToken is not null, books, refs, limitations);

    private static CatalogDiscoveryBookResponse ToResponse(ObservedBookRecord record) => new(
        record.BookSubjectId,
        record.Title,
        record.SourceKind?.ToString(),
        record.PublicationDate,
        record.PublicationDatePrecision?.ToString(),
        record.PublicationDateBasis?.ToString(),
        record.Credits.Select(credit => new CatalogDiscoveryCreditResponse(credit.Label, credit.SubjectId, credit.NativeAuthorId is not null, credit.Role, credit.RoleNotRetained)).ToArray(),
        record.Memberships.Select(membership => new CatalogDiscoveryMembershipResponse(
            membership.SubjectId, membership.Label, membership.NameMissing, membership.PositionLabel, membership.PositionSort,
            membership.Relationship, membership.CanonicalState, membership.NativeSeriesId is not null)).ToArray(),
        record.VersionLabel,
        record.Formats.Count > 0 ? record.Formats[0] : null,
        record.CreditsTruncated || record.MembershipsTruncated || record.FactsTruncated);

    private static CatalogDiscoveryRefResponse ToResponse(ObservedRef reference) => new(
        reference.SubjectId, reference.Kind.ToString(), reference.Label, reference.NameMissing, reference.NativeId is not null,
        reference.Relationship, reference.Disambiguation, reference.WorkCount, reference.IsCompleted, reference.PrimaryBooksCount,
        reference.CanonicalState);
}
