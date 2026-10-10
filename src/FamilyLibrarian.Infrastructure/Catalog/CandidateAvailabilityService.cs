using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FamilyLibrarian.Infrastructure.Catalog;

/// <summary>
/// Fans a <see cref="BookIdentity"/> out across every registered owned-library
/// and direct-acquisition provider (CWA, Audiobookshelf, Project Gutenberg,
/// and any enabled external providers) for both media types at once, all in
/// parallel via <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/> --
/// unlike <see cref="WorkFulfillmentOptionsService"/>'s sequential fan-out,
/// this runs every provider call concurrently since it's driven by an
/// interactive search results page rather than a single work-detail load.
/// The browser performs this enrichment after it has rendered catalog results
/// and cancels it when that search is superseded. A source is therefore
/// allowed to complete on the caller's lifetime rather than an arbitrary
/// server-side deadline that could turn a slow valid result into a false
/// absence.
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
    private static readonly RequestMediaType[] MediaTypes = [RequestMediaType.Ebook, RequestMediaType.Audiobook];

    public async Task<CandidateAvailabilityResult> GetAvailabilityAsync(
        BookIdentity identity, CancellationToken cancellationToken)
    {
        var options = new List<FulfillmentOption>();
        await foreach (var update in GetAvailabilityUpdatesAsync(identity, cancellationToken))
        {
            options.AddRange(update.Options);
        }

        return new CandidateAvailabilityResult(
            options.Where(option => option.MediaType == RequestMediaType.Ebook).ToArray(),
            options.Where(option => option.MediaType == RequestMediaType.Audiobook).ToArray());
    }

    public async IAsyncEnumerable<CandidateAvailabilityUpdate> GetAvailabilityUpdatesAsync(
        BookIdentity identity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Only the provider ids are captured from this request's own scope --
        // each task below re-resolves its provider inside a brand-new scope
        // rather than calling back into these instances directly.
        var ownedIds = ownedLibraryProviders.Select(provider => provider.Id).ToArray();
        var directIds = directAcquisitionProviders.Select(provider => provider.Id).ToArray();

        var updates = Channel.CreateUnbounded<CandidateAvailabilityUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var tasks = new List<Task>();

        foreach (var mediaType in MediaTypes)
        {
            foreach (var id in ownedIds)
            {
                tasks.Add(PublishAsync(SafeCallInScopeAsync(
                    scope => scope.ServiceProvider.GetServices<IOwnedLibraryProvider>().First(provider => provider.Id == id),
                    (provider, ct) => provider.FindOwnedMatchesAsync(identity, mediaType, ct),
                    cancellationToken), updates.Writer, cancellationToken));
            }

            foreach (var id in directIds)
            {
                tasks.Add(PublishAsync(SafeCallInScopeAsync(
                    scope => scope.ServiceProvider.GetServices<IDirectAcquisitionProvider>().First(provider => provider.Id == id),
                    (provider, ct) => provider.FindDirectAcquisitionsAsync(identity, mediaType, ct),
                    cancellationToken), updates.Writer, cancellationToken));
            }

            tasks.Add(PublishExternalAsync(identity, mediaType, updates.Writer, cancellationToken));
        }

        _ = CompleteAsync(tasks, updates.Writer);
        await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken))
        {
            yield return update;
        }
    }

    private static async Task PublishAsync(
        Task<IReadOnlyList<FulfillmentOption>> source,
        ChannelWriter<CandidateAvailabilityUpdate> writer,
        CancellationToken cancellationToken)
    {
        var options = await source;
        if (options.Count > 0)
        {
            await writer.WriteAsync(new CandidateAvailabilityUpdate(options), cancellationToken);
        }
    }

    private async Task PublishExternalAsync(
        BookIdentity identity,
        RequestMediaType mediaType,
        ChannelWriter<CandidateAvailabilityUpdate> writer,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<ExternalCandidateAvailabilityChecker>();
        await foreach (var options in checker.FindUpdatesAsync(identity, mediaType, cancellationToken))
        {
            await writer.WriteAsync(new CandidateAvailabilityUpdate(options), cancellationToken);
        }
    }

    private static async Task CompleteAsync(IReadOnlyList<Task> tasks, ChannelWriter<CandidateAvailabilityUpdate> writer)
    {
        try
        {
            await Task.WhenAll(tasks);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    /// <summary>
    /// Same "degrade one source to empty, never fail the whole check" posture
    /// <see cref="WorkFulfillmentOptionsService"/> uses. Interactive calls
    /// run until the browser/API caller cancels them, rather than inventing a
    /// false absence after a server-side deadline.
    /// </summary>
    private async Task<IReadOnlyList<FulfillmentOption>> SafeCallInScopeAsync<TService>(
        Func<IServiceScope, TService> resolve,
        Func<TService, CancellationToken, Task<IReadOnlyList<FulfillmentOption>>> call,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = resolve(scope);
            return await call(service, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A source-owned cancellation remains optional enrichment; a
            // caller-owned cancellation propagates so the browser can stop
            // work when its search has been superseded.
            return [];
        }
    }
}
