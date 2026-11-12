using System.Text.Json;
using FamilyLibrarian.Application.Integrations;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>The accepted shape of a catalog search, shared by every caller that starts one.</summary>
public static class CatalogSearchLimits
{
    public const int MinimumQueryLength = 2;
    public const int MaximumQueryLength = 200;

    /// <summary>True when the already-trimmed text is long enough and not too long to send to providers.</summary>
    public static bool IsValidQueryText(string? trimmedText) =>
        !string.IsNullOrWhiteSpace(trimmedText) && trimmedText.Length is >= MinimumQueryLength and <= MaximumQueryLength;

    public static bool IsValidPage(int page) => page is >= 1 and <= BookSearchQuery.MaximumPage;
}

public enum ProviderSearchFailureKind
{
    Unavailable,
    InvalidResponse,
    TimedOut,
    Unexpected
}

/// <summary>Why a provider produced no candidates. Callers decide how to log it; this layer does not.</summary>
public sealed record ProviderSearchFailure(ProviderSearchFailureKind Kind, Exception Exception);

public sealed record ProviderSearchResult(
    string ProviderId,
    string ProviderName,
    bool Succeeded,
    IReadOnlyList<BookCandidate> Candidates,
    bool HasMore,
    ProviderSearchFailure? Failure = null);

public sealed record CatalogProviderSearchStatus(string ProviderId, string ProviderName, bool Succeeded);

/// <summary>
/// Grouped, ranked candidates plus per-provider availability for one search page. <see cref="ProviderResults"/> keeps
/// each provider's raw result, including why a failed one failed, so the caller can log it.
/// </summary>
public sealed record CatalogSearchOutcome(
    IReadOnlyList<BookCandidate> Candidates,
    IReadOnlyList<CatalogProviderSearchStatus> Providers,
    int Page,
    bool HasMore,
    IReadOnlyList<ProviderSearchResult> ProviderResults);

/// <summary>
/// Catalog search orchestration: fan a query out to the currently active metadata providers, tolerate individual
/// provider failure, then group and rank the candidates. The HTTP endpoints, the progressive search runs and any
/// later caller (such as a conversation service) use this one implementation; none carries its own copy of the
/// fan-out, grouping or availability rules.
/// </summary>
public interface ICatalogSearchService
{
    /// <summary>Searches every active provider concurrently and composes the result.</summary>
    Task<CatalogSearchOutcome> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Searches one provider. A provider that is down, slow or returns malformed data yields an unsuccessful result
    /// with the reason in <see cref="ProviderSearchResult.Failure"/>; it never fails the whole search. Cancellation
    /// by <paramref name="cancellationToken"/> propagates. With <paramref name="tolerateUnexpectedFailures"/> an
    /// exception of any other kind is also converted to an unsuccessful result (progressive runs, which must never
    /// stop on one provider); without it such an exception propagates.
    /// </summary>
    Task<ProviderSearchResult> SearchProviderAsync(
        IBookMetadataProvider provider,
        BookSearchQuery query,
        bool tolerateUnexpectedFailures,
        CancellationToken cancellationToken);

    /// <summary>Groups and ranks the candidates of the given provider results and reports each provider's availability.</summary>
    CatalogSearchOutcome Compose(IReadOnlyList<ProviderSearchResult> providerResults, BookSearchQuery query);
}

public sealed class CatalogSearchService(IActiveMetadataProviderResolver providerResolver) : ICatalogSearchService
{
    public async Task<CatalogSearchOutcome> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
    {
        var providers = await providerResolver.GetActiveProvidersAsync(cancellationToken);
        var results = await Task.WhenAll(providers.Select(provider =>
            SearchProviderAsync(provider, query, tolerateUnexpectedFailures: false, cancellationToken)));
        return Compose(results, query);
    }

    public async Task<ProviderSearchResult> SearchProviderAsync(
        IBookMetadataProvider provider,
        BookSearchQuery query,
        bool tolerateUnexpectedFailures,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await provider.SearchAsync(query, cancellationToken);
            return new ProviderSearchResult(provider.Id, provider.DisplayName, true, page.Candidates, page.HasMore);
        }
        catch (HttpRequestException exception)
        {
            return Failed(provider, ProviderSearchFailureKind.Unavailable, exception);
        }
        catch (JsonException exception)
        {
            return Failed(provider, ProviderSearchFailureKind.InvalidResponse, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(provider, ProviderSearchFailureKind.TimedOut, exception);
        }
        catch (Exception exception) when (tolerateUnexpectedFailures && exception is not OperationCanceledException)
        {
            return Failed(provider, ProviderSearchFailureKind.Unexpected, exception);
        }
    }

    public CatalogSearchOutcome Compose(IReadOnlyList<ProviderSearchResult> providerResults, BookSearchQuery query) =>
        new(
            BookCandidateGrouper.GroupMatchingCandidates(
                providerResults.SelectMany(result => result.Candidates).ToArray(), query.Text),
            providerResults
                .Select(result => new CatalogProviderSearchStatus(result.ProviderId, result.ProviderName, result.Succeeded))
                .ToArray(),
            query.Page,
            providerResults.Any(result => result.Succeeded && result.HasMore),
            providerResults);

    private static ProviderSearchResult Failed(IBookMetadataProvider provider, ProviderSearchFailureKind kind, Exception exception) =>
        new(provider.Id, provider.DisplayName, false, [], false, new ProviderSearchFailure(kind, exception));
}
