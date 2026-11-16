using System.Text.Json;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// Administrator-only catalog enrichment. The role requirement and the anti-forgery filter for the whole area are declared here, once,
/// so the area's authorization posture reads in one place: a non-admin caller cannot reach any route in this group, and every state
/// change is anti-forgery protected. The host, not the client, is the authority.
/// </summary>
internal static class CatalogAdminEndpoints
{
    public static void MapCatalogAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/catalog")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        admin.MapPost("/works/{workId:guid}/refresh", RefreshWorkAsync);

        // Read-only inspection: no route below writes, calls a provider, or returns an entity or a raw provider payload.
        admin.MapGet("/works/{workId:guid}/sources", GetWorkSourcesAsync);
        admin.MapGet("/issues", GetIssuesAsync);
    }

    private static readonly IReadOnlyDictionary<string, EvidenceIssueCategory> Categories =
        new Dictionary<string, EvidenceIssueCategory>(StringComparer.Ordinal)
        {
            ["possible-duplicates"] = EvidenceIssueCategory.PossibleDuplicates,
            ["same-name-authors"] = EvidenceIssueCategory.SameNameAuthors,
            ["same-name-series"] = EvidenceIssueCategory.SameNameSeries,
            ["provisional-credits"] = EvidenceIssueCategory.ProvisionalCredits,
            ["unbound-series-claims"] = EvidenceIssueCategory.UnboundSeriesClaims,
            ["conflicted-positions"] = EvidenceIssueCategory.ConflictedPositions,
            ["contested-observations"] = EvidenceIssueCategory.ContestedObservations
        };

    private static async Task<IResult> GetWorkSourcesAsync(
        Guid workId,
        CatalogEvidenceInspector inspector,
        CancellationToken cancellationToken)
    {
        var view = await inspector.InspectWorkAsync(workId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(ToResponse(view));
    }

    /// <summary>One bounded page of one issue category. Every input is validated; an unknown category or a malformed cursor is a 400.</summary>
    private static async Task<IResult> GetIssuesAsync(
        string? category,
        string? cursor,
        int? take,
        CatalogEvidenceInspector inspector,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var parsed = EvidenceIssueCategory.PossibleDuplicates;
        var knownCategory = category is not null && Categories.TryGetValue(category, out parsed);
        if (!knownCategory)
        {
            errors["category"] = [$"Choose one of: {string.Join(", ", Categories.Keys)}."];
        }

        var pageSize = take ?? EvidenceInspectionLimits.DefaultIssuePageSize;
        if (pageSize is < 1 or > EvidenceInspectionLimits.MaximumIssuePageSize)
        {
            errors["take"] = [$"Enter a page size between 1 and {EvidenceInspectionLimits.MaximumIssuePageSize}."];
        }

        if (knownCategory && cursor is not null && (cursor.Length == 0 || !EvidenceCursor.IsValid(parsed, cursor)))
        {
            errors["cursor"] = ["The continuation is not valid. Start again without one."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var page = await inspector.ListIssuesAsync(parsed, cursor, pageSize, cancellationToken);
        return Results.Ok(new CatalogIssueReportResponse(
            category!,
            page.Items.Select(item => new CatalogIssueResponse(item.Kind, item.Subject, item.Reason, item.Ids, item.ObservedAtUtc)).ToArray(),
            page.NextCursor));
    }

    private static WorkSourcesAdminResponse ToResponse(WorkEvidenceView view) => new(
        view.Work.WorkId,
        view.Work.Title,
        view.Work.IsRetired,
        view.Work.VersionClass,
        view.Work.FirstPublicationDate,
        view.Work.FirstPublicationPrecision?.ToString(),
        view.Work.FirstPublicationBasis?.ToString(),
        view.References.Select(reference => new AdminSourceReferenceResponse(
            reference.ProviderId,
            reference.ProviderName,
            reference.ProviderConfigured ? "Configured" : "NotConfigured",
            reference.ExternalId,
            reference.ObservedAtUtc,
            reference.LastSeenAtUtc,
            reference.HasSnapshot ? "Snapshot" : "NoSnapshotRetained")).ToArray(),
        view.Observations.Select(observation => new AdminObservationResponse(
            observation.ProviderId,
            observation.ProviderName,
            observation.ProviderName is null ? "NotConfigured" : "Configured",
            observation.ExternalId,
            observation.ObservedAtUtc,
            observation.LastSeenAtUtc,
            observation.SchemaVersion,
            observation.ContentHash,
            observation.Outcome,
            observation.Reason,
            observation.Snapshot is null ? null : Parse(observation.Snapshot),
            observation.SnapshotTruncated)).ToArray(),
        view.ObservationsTruncated,
        view.Facts.Select(fact => new AdminFactResponse(
            fact.Field,
            fact.Scope,
            fact.Unbound,
            fact.SelectedValueJson is null ? null : Parse(fact.SelectedValueJson),
            fact.State,
            fact.DateConflict,
            fact.Claims.Select(claim => new AdminClaimResponse(
                claim.ProviderId,
                claim.ProviderName,
                claim.ProviderName is null && claim.ProviderId != "legacy-unattributed" ? "NotConfigured" : "Configured",
                claim.SourceEntityId,
                Parse(claim.ValueJson),
                claim.FirstSeenAtUtc,
                claim.LastSeenAtUtc,
                claim.IsCurrent,
                claim.IsSelected,
                claim.ContradictsSelected)).ToArray())).ToArray(),
        view.ClaimsTruncated);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Refreshes one Work from its stored sources and looks for other sources. Requests, ownership, notifications and reference
    /// ownership are untouched; nothing is removed and nobody is notified.
    /// </summary>
    private static async Task<IResult> RefreshWorkAsync(
        Guid workId,
        CatalogObservationService observation,
        CancellationToken cancellationToken)
    {
        var report = await observation.RefreshWorkAsync(workId, cancellationToken);
        return report.Status switch
        {
            WorkRefreshStatus.WorkNotFound => Results.NotFound(),
            WorkRefreshStatus.WorkRetired => Results.Problem(
                title: "Book was retired",
                detail: "This book was retired from the catalog, so it was not refreshed.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "catalog.work_retired" }),
            _ => Results.Ok(ToResponse(report))
        };
    }

    private static WorkRefreshResponse ToResponse(WorkRefreshReport report) => new(
        report.WorkId,
        report.Status.ToString(),
        report.Providers.Select(step => new WorkRefreshStepResponse(
            step.ProviderId, step.ExternalId, step.Phase, step.Status.ToString(), step.Reason, step.ContradictedIsbns ?? [])).ToArray(),
        report.Proposals.Select(proposal => new TentativeProposalResponse(
            proposal.ProviderId, proposal.ExternalId, proposal.Title, proposal.Authors, proposal.Reason)).ToArray());
}
