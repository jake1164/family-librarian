using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Catalog;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Read-only author and series browsing under <c>/api/v1/catalog/browse</c>. Saved routes read the shared catalog by
/// stable id; context routes read one signed-in user's own temporary observations. No route calls a metadata provider,
/// resolves a candidate, or writes anything.
/// </summary>
internal static class CatalogBrowseEndpoints
{
    public static void MapCatalogBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        var browse = app.MapGroup("/api/v1/catalog/browse")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        browse.MapGet("/authors/{authorId:guid}", GetAuthorAsync);
        browse.MapGet("/series/{seriesId:guid}", GetSeriesAsync);
        browse.MapGet("/contexts/{contextId:guid}/authors/{subjectId:guid}", GetObservedAuthorAsync);
        browse.MapGet("/contexts/{contextId:guid}/series/{subjectId:guid}", GetObservedSeriesAsync);
        browse.MapGet("/contexts/{contextId:guid}/books/{providerId}", GetObservedBookAsync);
    }

    private static async Task<IResult> GetAuthorAsync(
        Guid authorId, int? page, CatalogBrowseService service, CancellationToken cancellationToken)
    {
        var result = await service.GetAuthorAsync(authorId, page ?? 1, cancellationToken);
        return result.Status != CatalogBrowseStatus.Ok
            ? Failure(result.Status)
            : Results.Ok(ToResponse(result.Value!));
    }

    private static async Task<IResult> GetSeriesAsync(
        Guid seriesId, int? page, CatalogBrowseService service, CancellationToken cancellationToken)
    {
        var result = await service.GetSeriesAsync(seriesId, page ?? 1, cancellationToken);
        return result.Status != CatalogBrowseStatus.Ok
            ? Failure(result.Status)
            : Results.Ok(ToResponse(result.Value!));
    }

    private static async Task<IResult> GetObservedAuthorAsync(
        Guid contextId,
        Guid subjectId,
        int? page,
        ICurrentUser currentUser,
        IClock clock,
        CatalogBrowseContextStore store,
        CatalogBrowseService service,
        CancellationToken cancellationToken)
    {
        if (!CatalogBrowseService.IsValidPage(page ?? 1)) return Failure(CatalogBrowseStatus.InvalidPage);
        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();

        // Unknown, expired, evicted, another owner's and wrong-kind all answer the same 404.
        var sample = store.TryGetSample(ownerId, contextId, ObservedSubjectKind.Author, subjectId, clock.UtcNow);
        if (sample is null) return Results.NotFound();

        var result = await service.PreviewAuthorAsync(sample, page ?? 1, cancellationToken);
        return result.Status != CatalogBrowseStatus.Ok
            ? Failure(result.Status)
            : Results.Ok(ToResponse(contextId, result.Value!));
    }

    private static async Task<IResult> GetObservedSeriesAsync(
        Guid contextId,
        Guid subjectId,
        int? page,
        ICurrentUser currentUser,
        IClock clock,
        CatalogBrowseContextStore store,
        CatalogBrowseService service,
        CancellationToken cancellationToken)
    {
        if (!CatalogBrowseService.IsValidPage(page ?? 1)) return Failure(CatalogBrowseStatus.InvalidPage);
        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();

        var sample = store.TryGetSample(ownerId, contextId, ObservedSubjectKind.Series, subjectId, clock.UtcNow);
        if (sample is null) return Results.NotFound();

        var result = await service.PreviewSeriesAsync(sample, page ?? 1, cancellationToken);
        return result.Status != CatalogBrowseStatus.Ok
            ? Failure(result.Status)
            : Results.Ok(ToResponse(contextId, result.Value!));
    }

    private static IResult GetObservedBookAsync(
        Guid contextId,
        string providerId,
        string? externalId,
        ICurrentUser currentUser,
        IClock clock,
        CatalogBrowseContextStore store)
    {
        var limits = store.Limits;
        var problems = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > limits.MaxProviderIdLength)
            problems["providerId"] = ["The provider reference is invalid."];
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > limits.MaxExternalIdLength)
            problems["externalId"] = ["The book reference is invalid."];
        if (problems.Count > 0) return Results.ValidationProblem(problems);
        if (currentUser.UserId is not { } ownerId) return Results.Unauthorized();

        var found = store.TryGetRecord(ownerId, contextId, providerId, externalId!, clock.UtcNow);
        if (found is null) return Results.NotFound();

        var limitations = new List<string> { CatalogBrowseLimitations.SourceSample };
        if (found.Record.CreditsTruncated || found.Record.MembershipsTruncated || found.Record.FactsTruncated)
            limitations.Add(CatalogBrowseLimitations.Truncated);
        return Results.Ok(new CatalogObservedBookResponse(
            contextId, ToResponse(CatalogBrowseService.ToBook(found.Record)), found.Record.ObservedAtUtc, found.ExpiresAtUtc, limitations));
    }

    /// <summary>Bad page input is a 400; an unauthenticated principal never reaches here past the host's own check.</summary>
    private static IResult Failure(CatalogBrowseStatus status) => status switch
    {
        CatalogBrowseStatus.NotFound => Results.NotFound(),
        CatalogBrowseStatus.Unauthenticated => Results.Unauthorized(),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["page"] = [$"Enter a page between 1 and {CatalogBrowseService.MaximumPage}."]
        })
    };

    private static CatalogAuthorBrowseResponse ToResponse(BrowseAuthorPage page) => new(
        page.AuthorId, page.Name, page.Page, page.HasMore,
        page.Groups.Select(ToResponse).ToArray(),
        page.Ungrouped.Select(ToResponse).ToArray(),
        page.Limitations);

    private static CatalogSeriesBrowseResponse ToResponse(BrowseSeriesPage page) => new(
        page.SeriesId, page.Name, page.Page, page.HasMore,
        page.Entries.Select(ToResponse).ToArray(),
        page.Limitations);

    private static CatalogObservedAuthorBrowseResponse ToResponse(Guid contextId, ObservedAuthorPreview preview) => new(
        contextId, preview.Subject.SubjectId, preview.Subject.Label, preview.Subject.ProviderId,
        preview.Subject.ProviderName, preview.Subject.AnchorExternalId, preview.ObservedAtUtc, preview.ExpiresAtUtc,
        preview.Page, preview.HasMore,
        preview.Groups.Select(ToResponse).ToArray(),
        preview.Ungrouped.Select(ToResponse).ToArray(),
        ToResponse(preview.NameChoices),
        preview.Limitations);

    private static CatalogObservedSeriesBrowseResponse ToResponse(Guid contextId, ObservedSeriesPreview preview) => new(
        contextId, preview.Subject.SubjectId, preview.Subject.Label, preview.Subject.ProviderId,
        preview.Subject.ProviderName, preview.Subject.AnchorExternalId, preview.ObservedAtUtc, preview.ExpiresAtUtc,
        preview.Page, preview.HasMore,
        preview.Entries.Select(ToResponse).ToArray(),
        ToResponse(preview.NameChoices),
        preview.Limitations);

    private static CatalogBrowseNameChoicesResponse ToResponse(BrowseNameChoices choices) => new(
        choices.Choices.Select(choice => new CatalogBrowseNameChoiceResponse(
            choice.Id, choice.Name, choice.KnownBookCount, choice.SampleTitles)).ToArray(),
        choices.Truncated);

    private static CatalogBrowseGroupResponse ToResponse(BrowseGroup group) => new(
        group.SeriesId, group.SubjectId, group.Name, group.Entries.Select(ToResponse).ToArray());

    private static CatalogBrowseEntryResponse ToResponse(BrowseEntry entry) => new(
        ToResponse(entry.Book), entry.PositionLabel, entry.Position);

    private static CatalogBrowseBookResponse ToResponse(BrowseBook book) => new(
        book.WorkId, book.ProviderId, book.ExternalId, book.Title, book.PublicationDate,
        book.Credits.Select(credit => new CatalogBrowseCreditResponse(
            credit.AuthorId, credit.SubjectId, credit.Name, credit.Role)).ToArray(),
        book.Memberships.Select(membership => new CatalogBrowseMembershipResponse(
            membership.SeriesId, membership.SubjectId, membership.Name, membership.PositionLabel,
            membership.Position, membership.IsPrimary)).ToArray(),
        book.Sources.Select(source => new CatalogBrowseSourceResponse(
            source.ProviderId, source.ProviderName, source.ObservedAtUtc)).ToArray(),
        book.Formats, book.VersionKind, book.VersionLabel, book.AssociationsTruncated);
}
