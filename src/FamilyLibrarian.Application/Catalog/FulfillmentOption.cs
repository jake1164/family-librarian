using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// One permitted way to get a Work in a given format, from one provider.
/// </summary>
/// <remarks>
/// An option is not an authorization to spend, borrow, or download — it is
/// display/decision plumbing only. M8 defines this shape and the capability
/// interfaces below with zero concrete implementations: no real
/// availability/store/direct-acquisition provider exists until M11, so
/// <see cref="IWorkFulfillmentOptionsService"/> currently returns only the
/// baseline <see cref="OptionKind.Owned"/>/not-owned state, not real offers.
/// </remarks>
public sealed record FulfillmentOption(
    string ProviderId,
    string ProviderResultId,
    // Guid.Empty when computed from a raw BookIdentity (a search candidate
    // with no persisted Work yet) rather than resolved for a real Work.
    Guid WorkId,
    Guid? EditionId,
    RequestMediaType MediaType,
    OptionKind OptionKind,
    AcquisitionMethod AcquisitionMethod,
    string? Format,
    string? Language,
    string? Quality,
    string? Availability,
    decimal? Cost,
    string? Currency,
    string? LicenseOrUsageStatus,
    string? DrmStatus,
    Uri? ExternalActionUri,
    string? ProviderData,
    // Meaningful for OptionKind.Owned (how confidently the owning provider
    // matched this artifact to the requested Work) and for a
    // DirectAcquisition option from an admin-registered external provider
    // (how confidently ExternalProviderMatchVerifier confirmed the result
    // against the requested title/author/ISBN -- an external provider is
    // unvetted third-party code, so its own claimed title/author is never
    // trusted alone). Null for every other OptionKind, and for an Owned or
    // DirectAcquisition option from a provider that doesn't go through the
    // shared matcher (e.g. Gutenberg, which uses its own IBookMatcher calls
    // directly). See BookMatchBasis for why this exists: a title/author
    // fallback match is a reviewable guess, not a verified identity, and a
    // consumer that acts on a match automatically (e.g. the Kindle
    // existing-book send, or fetching a DirectAcquisition file) must not
    // treat the two the same way.
    BookMatchBasis? MatchBasis = null,
    // True when the only kind of match found was excluded by
    // LanguageAcceptance (ACCURACY-1) -- either an IAutomaticDirectAcquisitionProvider's
    // title/author match, or an external provider's result via
    // ExternalProviderMatchVerifier. Such an option must never be
    // auto-acquired or fetched without confirmation; a caller instead offers
    // it as a SELFSERV-1-style preference decision ("get it anyway, or keep
    // looking?").
    bool RequiresLanguageConfirmation = false,
    // This specific candidate's own title/author, when the provider can
    // supply one distinct from the canonical Work title -- e.g. a specific
    // Gutenberg edition. Null for a provider that only ever returns a
    // single already-verified match (an Owned lookup): those need no
    // disambiguation from the Work they already matched. Used only to label
    // a RequestReviewCandidate distinctly (SELFSERV-1); never persisted
    // beyond that review.
    string? Title = null,
    string? Author = null,
    // Carried unchanged from the search candidate that produced this option
    // (protocol v2 §8/§9) so a later /acquire call can detect staleness or
    // resolve efficiently -- never inspected or modified, and null for
    // every provider that doesn't return one.
    string? CandidateRevision = null,
    string? AcquireToken = null,
    // ExternalReleasePolicy's verdict on this option's release evidence
    // (protocol v2 §7/§10/§16) -- independent of MatchBasis. A release
    // problem (a collection, a sample, an abridged mismatch) must block
    // automatic acquisition and require the same explicit confirmation as a
    // low-confidence match, even when MatchBasis is Identifier: a correct
    // ISBN on an omnibus edition is still an omnibus.
    bool RequiresReleaseConfirmation = false,
    string? ReleaseConcern = null);

public enum OptionKind
{
    Owned,
    Availability,
    StoreOffer,
    DirectAcquisition,
    ExternalAction
}

public enum AcquisitionMethod
{
    Borrow,
    Purchase,
    DirectDownload,
    ManualImport,
    OwnedImport,
    ProviderManaged
}

/// <summary>
/// The identity a provider needs to check for a match, independent of
/// whether the book has been resolved into a persisted Work yet -- lets a
/// raw catalog search result be checked the same way a Work is.
/// </summary>
/// <remarks>
/// <see cref="Title"/>/<see cref="Author"/>/<see cref="Isbn13Candidates"/>
/// remain the fields every existing matcher (Gutenberg, CWA, Audiobookshelf)
/// actually reads -- they stay simple on purpose. <see cref="Authors"/>/
/// <see cref="Series"/>/<see cref="Language"/>/<see cref="PublicationYear"/>/
/// <see cref="Publisher"/> exist only to build a protocol v2 §6 search
/// request's richer <c>work</c>/<c>edition</c> evidence for an admin-registered
/// external provider -- optional, and <c>null</c>/empty for a caller that has
/// nothing richer to offer.
/// </remarks>
public sealed record BookIdentity(
    string Title,
    string? Author,
    IReadOnlyCollection<string> Isbn13Candidates,
    IReadOnlyList<BookAuthor>? Authors = null,
    IReadOnlyList<BookSeries>? Series = null,
    string? Language = null,
    int? PublicationYear = null,
    string? Publisher = null);

/// <summary>Advertises store-offer discovery. No concrete implementation ships in M8.</summary>
public interface IStoreOfferProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindOffersAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

/// <summary>Advertises library/subscription availability. No concrete implementation ships in M8.</summary>
public interface IAvailabilityProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindAvailabilityAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

/// <summary>Advertises free/direct legal acquisition, and can fetch the file for an option it returned.</summary>
public interface IDirectAcquisitionProvider
{
    string Id { get; }

    /// <summary>
    /// Whether this provider currently has enough of its own data/connection
    /// in place to give a meaningful answer. A provider backed by a source
    /// still being (re)built (e.g. a local catalogue mid-import) is not
    /// broken, just not ready yet — the default of <see langword="true"/>
    /// covers every provider with nothing like that to report.
    /// </summary>
    /// <remarks>
    /// This exists so the automatic fulfillment loop can skip a not-yet-ready
    /// provider without recording a lookup at all: a "no match" taken while a
    /// source is still filling itself in is not a real answer, and recording
    /// one would falsely start that provider's retry cooldown for this
    /// format — the same request would then not be tried again until the
    /// cooldown expired, even once the source became ready moments later.
    /// </remarks>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="IDirectAcquisitionProvider.FindDirectAcquisitionsAsync(Guid, RequestMediaType, CancellationToken)"/>,
    /// but for a raw catalog search candidate that has no persisted Work yet
    /// -- returned options carry <see cref="FulfillmentOption.WorkId"/> as
    /// <see cref="Guid.Empty"/>.
    /// </summary>
    Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
        BookIdentity identity,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the file(s) for a previously returned option — more than one
    /// for a multi-track acquisition (e.g. a chaptered audiobook), which the
    /// caller stages as one bundle rather than independent artifacts.
    /// <paramref name="fulfillmentOption"/> should be freshly re-derived by
    /// the caller (e.g. via <see cref="FindDirectAcquisitionsAsync(Guid, RequestMediaType, CancellationToken)"/>), never
    /// reconstructed from client-supplied data — <see cref="FulfillmentOption.ProviderData"/>
    /// carries whatever this provider needs (e.g. a resolved download URL),
    /// opaque to every caller but this one.
    /// </summary>
    Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(FulfillmentOption fulfillmentOption, CancellationToken cancellationToken);
}

/// <summary>
/// A direct-acquisition provider whose returned options are conservative enough
/// for the server to fetch without a librarian choosing among them first.
/// </summary>
/// <remarks>
/// This is deliberately an opt-in capability rather than an inference from
/// <see cref="FulfillmentOption.OptionKind"/> or price. A provider must make
/// its own title/creator/identifier confidence decision before it implements
/// this contract. The normal security and identity checks still run after the
/// file is fetched.
/// </remarks>
public interface IAutomaticDirectAcquisitionProvider : IDirectAcquisitionProvider;

public sealed record DirectAcquisitionFile(Stream Content, string Filename);

/// <summary>Advertises matches in a linked owned library (e.g. Calibre-Web). No concrete implementation ships in M8.</summary>
public interface IOwnedLibraryProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="FindOwnedMatchesAsync(Guid, RequestMediaType, CancellationToken)"/>,
    /// but for a raw catalog search candidate that has no persisted Work yet
    /// -- returned options carry <see cref="FulfillmentOption.WorkId"/> as
    /// <see cref="Guid.Empty"/>.
    /// </summary>
    Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        BookIdentity identity,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

public interface IWorkFulfillmentOptionsService
{
    Task<IReadOnlyList<FulfillmentOption>> GetOptionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Aggregates whatever capability providers are registered. Today that is none,
/// so this returns an empty list — the plumbing exists so a real provider in
/// M11 is additive, not a redesign.
/// </summary>
public sealed class WorkFulfillmentOptionsService(
    IEnumerable<IAvailabilityProvider> availabilityProviders,
    IEnumerable<IStoreOfferProvider> storeOfferProviders,
    IEnumerable<IDirectAcquisitionProvider> directAcquisitionProviders,
    IEnumerable<IOwnedLibraryProvider> ownedLibraryProviders,
    ExternalCandidateAvailabilityChecker externalProviderChecker,
    IWorkLookup workLookup) : IWorkFulfillmentOptionsService
{
    public async Task<IReadOnlyList<FulfillmentOption>> GetOptionsAsync(
        Guid workId,
        RequestMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var options = new List<FulfillmentOption>();

        foreach (var provider in availabilityProviders)
        {
            try
            {
                options.AddRange(await provider.FindAvailabilityAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // Availability is optional page enrichment. One unavailable
                // provider must not make the Work or request page unavailable.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Treat a provider's own timeout as no options. Caller-requested
                // cancellation still propagates through the filter above.
            }
        }

        foreach (var provider in storeOfferProviders)
        {
            try
            {
                options.AddRange(await provider.FindOffersAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // Store offers are optional page enrichment.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider timeout degrades to no offers from that provider.
            }
        }

        foreach (var provider in directAcquisitionProviders)
        {
            try
            {
                options.AddRange(await provider.FindDirectAcquisitionsAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // The automatic worker records provider failures separately.
                // This read model only needs to omit unavailable options.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider timeout must not fail the containing page.
            }
        }

        foreach (var provider in ownedLibraryProviders)
        {
            try
            {
                options.AddRange(await provider.FindOwnedMatchesAsync(workId, mediaType, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // Owned-library status is optional page enrichment.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider timeout degrades to an unknown owned status.
            }
        }

        try
        {
            options.AddRange(await FindExternalProviderOptionsAsync(workId, mediaType, cancellationToken));
        }
        catch (HttpRequestException)
        {
            // An external provider's own search failure degrades to no
            // options from it, same as the four loops above.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A provider timeout must not fail the containing page.
        }

        return options;
    }

    /// <summary>
    /// Resolves the Work into a <see cref="BookIdentity"/> and delegates to
    /// <see cref="ExternalCandidateAvailabilityChecker"/> — the same identity
    /// -based check a raw catalog search candidate uses — then stamps the
    /// real <paramref name="workId"/> onto whatever it finds.
    /// </summary>
    private async Task<IReadOnlyList<FulfillmentOption>> FindExternalProviderOptionsAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null)
        {
            return [];
        }

        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s);
        var found = await externalProviderChecker.FindAsync(identity, mediaType, cancellationToken);
        return found.Select(option => option with { WorkId = workId }).ToArray();
    }
}
