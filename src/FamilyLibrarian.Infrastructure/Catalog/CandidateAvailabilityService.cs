using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Catalog;

/// <summary>
/// Fans a <see cref="BookIdentity"/> out across every registered owned-library
/// and direct-acquisition provider (CWA, Audiobookshelf, Project Gutenberg,
/// and any enabled external providers) for both media types at once, all in
/// parallel via <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/> --
/// unlike <see cref="WorkFulfillmentOptionsService"/>'s sequential fan-out,
/// this runs every provider call concurrently since it's driven by an
/// interactive search results page rather than a single work-detail load.
/// A per-call bounded timeout keeps one unresponsive source (e.g. a CWA
/// server that has stopped answering) from holding up the rest of a badge
/// check indefinitely.
/// </summary>
/// <remarks>
/// Each parallel branch resolves its provider fresh from its own
/// <see cref="IServiceScopeFactory"/> scope rather than reusing the providers
/// injected into this class: EF Core's <c>DbContext</c> is request-scoped and
/// not safe for concurrent use, so running every provider's own settings-
/// store read against the same instance (as <c>Task.WhenAll</c> over the
/// injected providers directly would) throws
/// <see cref="InvalidOperationException"/> the moment two of them overlap.
/// A fresh scope per branch gives each its own <c>DbContext</c> instead.
/// </remarks>
public sealed class CandidateAvailabilityService(
    IEnumerable<IOwnedLibraryProvider> ownedLibraryProviders,
    IEnumerable<IDirectAcquisitionProvider> directAcquisitionProviders,
    IServiceScopeFactory scopeFactory) : ICandidateAvailabilityService
{
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(10);
    private static readonly RequestMediaType[] MediaTypes = [RequestMediaType.Ebook, RequestMediaType.Audiobook];

    public async Task<CandidateAvailabilityResult> GetAvailabilityAsync(
        BookIdentity identity, CancellationToken cancellationToken)
    {
        // Only the provider ids are captured from this request's own scope --
        // each task below re-resolves its provider inside a brand-new scope
        // rather than calling back into these instances directly.
        var ownedIds = ownedLibraryProviders.Select(provider => provider.Id).ToArray();
        var directIds = directAcquisitionProviders.Select(provider => provider.Id).ToArray();

        var tasks = new List<Task<IReadOnlyList<FulfillmentOption>>>();

        foreach (var mediaType in MediaTypes)
        {
            foreach (var id in ownedIds)
            {
                tasks.Add(SafeCallInScopeAsync(
                    scope => scope.ServiceProvider.GetServices<IOwnedLibraryProvider>().First(provider => provider.Id == id),
                    (provider, ct) => provider.FindOwnedMatchesAsync(identity, mediaType, ct),
                    cancellationToken));
            }

            foreach (var id in directIds)
            {
                tasks.Add(SafeCallInScopeAsync(
                    scope => scope.ServiceProvider.GetServices<IDirectAcquisitionProvider>().First(provider => provider.Id == id),
                    (provider, ct) => provider.FindDirectAcquisitionsAsync(identity, mediaType, ct),
                    cancellationToken));
            }

            tasks.Add(SafeCallInScopeAsync(
                scope => scope.ServiceProvider.GetRequiredService<ExternalCandidateAvailabilityChecker>(),
                (checker, ct) => checker.FindAsync(identity, mediaType, ct),
                cancellationToken));
        }

        var results = await Task.WhenAll(tasks);
        var options = results.SelectMany(result => result).ToArray();

        return new CandidateAvailabilityResult(
            options.Where(option => option.MediaType == RequestMediaType.Ebook).ToArray(),
            options.Where(option => option.MediaType == RequestMediaType.Audiobook).ToArray());
    }

    /// <summary>
    /// Same "degrade one source to empty, never fail the whole check" posture
    /// <see cref="WorkFulfillmentOptionsService"/> uses, plus a per-call
    /// timeout so a source that never fails but never answers either (e.g. a
    /// black-holed CWA server) can't tie up one badge check indefinitely.
    /// </summary>
    private async Task<IReadOnlyList<FulfillmentOption>> SafeCallInScopeAsync<TService>(
        Func<IServiceScope, TService> resolve,
        Func<TService, CancellationToken, Task<IReadOnlyList<FulfillmentOption>>> call,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerCallTimeout);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = resolve(scope);
            return await call(service, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Either the caller's own cancellation was already handled by the
            // guard above, or this source simply took longer than
            // PerCallTimeout -- both degrade to "no options from this source".
            return [];
        }
    }
}
