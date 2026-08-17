using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Policy;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Policy;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Web.Logging;

namespace FamilyLibrarian.Web.Endpoints;

internal static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // The catalog stopped being an anonymous development surface when requests
        // arrived: a request needs a server-verified owner, and searching now sends
        // family search terms to third-party providers on an identified user's behalf.
        var catalog = app.MapGroup("/api/v1/catalog")
            .RequireAuthorization()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        catalog.MapGet("/search", SearchCatalogAsync);
        catalog.MapGet("/candidates/{providerId}/{externalId}", GetCatalogCandidateAsync);
        catalog.MapPost("/candidates/{providerId}/{externalId}/resolve", ResolveCatalogCandidateAsync);
        catalog.MapGet("/works/{workId:guid}", GetCatalogWorkAsync);
        catalog.MapGet("/works/{workId:guid}/fulfillment-options", GetWorkFulfillmentOptionsAsync);
    }

    private static async Task<IResult> SearchCatalogAsync(
        string? q,
        IActiveMetadataProviderResolver providerResolver,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var searchText = q?.Trim();
        if (string.IsNullOrWhiteSpace(searchText) || searchText.Length is < 2 or > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["q"] = ["Enter between 2 and 200 characters to search the catalog."]
            });
        }

        var query = new BookSearchQuery(searchText);
        var logger = loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch");
        var providers = await providerResolver.GetActiveProvidersAsync(cancellationToken);
        var searches = providers.Select(provider =>
            SearchProviderAsync(provider, query, logger, cancellationToken));
        var providerResults = await Task.WhenAll(searches);

        return Results.Ok(new CatalogSearchResponse(
            BookCandidateGrouper.GroupExactIsbnMatches(providerResults
                .Where(result => result.Succeeded)
                .SelectMany(result => result.Candidates)
                .ToArray())
                .Select(ToResponse)
                .ToArray(),
            providerResults
                .Select(result => new CatalogProviderSearchStatusResponse(
                    result.ProviderId,
                    result.ProviderName,
                    result.Succeeded))
                .ToArray()));
    }

    private static async Task<IResult> GetCatalogCandidateAsync(
        string providerId,
        string externalId,
        IActiveMetadataProviderResolver providerResolver,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Resolving through the active set means a disabled provider cannot be
        // reached by addressing its id directly.
        var provider = await providerResolver.FindActiveProviderAsync(providerId, cancellationToken);
        if (provider is null)
        {
            return Results.NotFound();
        }

        try
        {
            var candidate = await provider.GetDetailsAsync(externalId, cancellationToken);
            return candidate is null ? Results.NotFound() : Results.Ok(ToResponse(candidate));
        }
        catch (HttpRequestException exception)
        {
            LogProviderFailure(loggerFactory, provider, exception);
            return Results.Problem(
                title: "Catalog provider unavailable",
                detail: "The selected catalog source is temporarily unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (JsonException exception)
        {
            LogProviderFailure(loggerFactory, provider, exception);
            return Results.Problem(
                title: "Catalog provider unavailable",
                detail: "The selected catalog source returned an invalid response.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogProviderFailure(loggerFactory, provider, exception);
            return Results.Problem(
                title: "Catalog provider unavailable",
                detail: "The selected catalog source timed out.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ResolveCatalogCandidateAsync(
        string providerId,
        string externalId,
        CatalogWorkResolver resolver,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await resolver.ResolveAsync(providerId, externalId, cancellationToken);
            var response = await ToWorkResponseAsync(result.Work, resolver, cancellationToken);
            return result.WasCreated
                ? Results.Created($"/api/v1/catalog/works/{result.Work.Id}", response)
                : Results.Ok(response);
        }
        catch (UnknownMetadataProviderException)
        {
            return Results.NotFound();
        }
        catch (CatalogCandidateNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["providerReference"] = ["The provider reference is invalid."]
            });
        }
        catch (HttpRequestException exception)
        {
            MetadataProviderLog.CandidateDetailsUnavailable(
                loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
                providerId,
                exception);
            return Results.Problem(
                title: "Catalog provider unavailable",
                detail: "The selected catalog source is temporarily unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (JsonException exception)
        {
            MetadataProviderLog.CandidateDetailsUnavailable(
                loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
                providerId,
                exception);
            return Results.Problem(
                title: "Catalog provider unavailable",
                detail: "The selected catalog source returned an invalid response.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            MetadataProviderLog.CandidateDetailsUnavailable(
                loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
                providerId,
                exception);
            return Results.Problem(
                title: "Catalog provider unavailable",
                detail: "The selected catalog source timed out.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetCatalogWorkAsync(
        Guid workId,
        CatalogWorkResolver resolver,
        CancellationToken cancellationToken)
    {
        var work = await resolver.GetWorkAsync(workId, cancellationToken);
        return work is null
            ? Results.NotFound()
            : Results.Ok(await ToWorkResponseAsync(work, resolver, cancellationToken));
    }

    private static async Task<IResult> GetWorkFulfillmentOptionsAsync(
        Guid workId,
        IWorkFulfillmentOptionsService fulfillment,
        AcquisitionPolicyService policyService,
        IPolicyRanker ranker,
        CancellationToken cancellationToken)
    {
        var ebook = await fulfillment.GetOptionsAsync(workId, RequestMediaType.Ebook, cancellationToken);
        var audiobook = await fulfillment.GetOptionsAsync(workId, RequestMediaType.Audiobook, cancellationToken);
        var profileId = await policyService.GetEffectiveProfileIdAsync(cancellationToken);

        return Results.Ok(new WorkFulfillmentOptionsResponse(
            ebook.Select(ToFulfillmentOptionResponse).ToArray(),
            audiobook.Select(ToFulfillmentOptionResponse).ToArray(),
            ToRecommendationResponse(ranker.Recommend(ebook, profileId)),
            ToRecommendationResponse(ranker.Recommend(audiobook, profileId))));
    }

    private static FulfillmentOptionResponse ToFulfillmentOptionResponse(FulfillmentOption option) => new(
        option.ProviderId,
        option.ProviderResultId,
        option.OptionKind.ToString(),
        option.AcquisitionMethod.ToString(),
        option.ExternalActionUri?.ToString());

    private static RecommendationResponse? ToRecommendationResponse(FulfillmentRecommendation? recommendation) =>
        recommendation is null
            ? null
            : new RecommendationResponse(
                recommendation.Option.ProviderId,
                recommendation.Option.ProviderResultId,
                recommendation.ProfileId,
                recommendation.Reason);

    private static async Task<ProviderSearchResult> SearchProviderAsync(
        IBookMetadataProvider provider,
        BookSearchQuery query,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await provider.SearchAsync(query, cancellationToken);
            return new ProviderSearchResult(
                provider.Id,
                provider.DisplayName,
                true,
                candidates);
        }
        catch (HttpRequestException exception)
        {
            MetadataProviderLog.SearchUnavailable(logger, provider.Id, exception);
        }
        catch (JsonException exception)
        {
            MetadataProviderLog.SearchReturnedInvalidJson(logger, provider.Id, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            MetadataProviderLog.SearchTimedOut(logger, provider.Id, exception);
        }

        return new ProviderSearchResult(provider.Id, provider.DisplayName, false, []);
    }

    private static void LogProviderFailure(
        ILoggerFactory loggerFactory,
        IBookMetadataProvider provider,
        Exception exception) =>
        MetadataProviderLog.CandidateDetailsUnavailable(
            loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
            provider.Id,
            exception);

    private static CatalogBookCandidateResponse ToResponse(BookCandidate candidate) => new(
        candidate.ProviderId,
        candidate.ProviderName,
        candidate.ExternalId,
        candidate.Title,
        candidate.Authors,
        candidate.Description,
        candidate.CoverUrl,
        candidate.PublicationDate,
        candidate.Editions.Select(edition => new CatalogEditionResponse(
            edition.Title,
            edition.Isbn13,
            edition.Format,
            edition.PublicationDate)).ToArray(),
        candidate.Series.Select(series => new CatalogSeriesResponse(
            series.Name,
            series.PositionLabel,
            series.IsPrimary)).ToArray());

    private static async Task<CatalogWorkResponse> ToWorkResponseAsync(
        Domain.Catalog.Work work,
        CatalogWorkResolver resolver,
        CancellationToken cancellationToken)
    {
        var sources = await resolver.GetWorkSourcesAsync(work.Id, cancellationToken);
        return new CatalogWorkResponse(
            work.Id,
            work.CanonicalTitle,
            work.Authors
                .OrderBy(author => author.Ordinal)
                .Select(author => author.Author.CanonicalName)
                .ToArray(),
            work.Description,
            work.CoverUrl,
            work.FirstPublicationDate,
            work.Editions
                .OrderBy(edition => edition.PublicationDate)
                .ThenBy(edition => edition.Title, StringComparer.Ordinal)
                .Select(edition => new CatalogEditionResponse(
                    edition.Title,
                    edition.Isbn13,
                    edition.Format.ToString(),
                    edition.PublicationDate))
                .ToArray(),
            work.SeriesEntries
                .OrderByDescending(entry => entry.IsPrimary)
                .ThenBy(entry => entry.PositionSort)
                .ThenBy(entry => entry.PositionLabel, StringComparer.Ordinal)
                .Select(entry => new CatalogSeriesResponse(
                    entry.Series.Name,
                    entry.PositionLabel,
                    entry.IsPrimary))
                .ToArray(),
            sources.Select(source => new CatalogWorkSourceResponse(
                source.ProviderId,
                source.ExternalId,
                source.ObservedAtUtc)).ToArray());
    }

    private sealed record ProviderSearchResult(
        string ProviderId,
        string ProviderName,
        bool Succeeded,
        IReadOnlyList<BookCandidate> Candidates);
}
