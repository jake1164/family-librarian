using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Web.Catalog;

/// <summary>The explicit source lookups a person can start. Each maps onto exactly one shared discovery operation.</summary>
public enum ExpansionKind
{
    BookSeries = 1,
    SeriesMembers = 2,
    ResolveAuthor = 3,
    AuthorWorks = 4,
    AuthorSeries = 5
}

public enum ExpansionStatus
{
    Ok = 0,

    /// <summary>Unknown, expired, evicted, another owner's or not a subject of this lookup: all one answer, so none can be probed.</summary>
    NotFound = 1,

    /// <summary>The subject is real but this lookup does not apply to it (a series is not an author).</summary>
    BadScope = 2,

    /// <summary>The source is disabled, removed or has no credential. No provider call was made.</summary>
    SourceUnavailable = 3,

    /// <summary>The source does not implement this lookup, or the API has no such operation. No provider call was made.</summary>
    Unsupported = 4,

    RateLimited = 5,

    Busy = 6,

    /// <summary>The journey that owns the allowance ended (absolute expiry or its operation limit). No provider call was made.</summary>
    JourneyEnded = 7,

    /// <summary>There is no continuation to follow from this window.</summary>
    NoContinuation = 8
}

/// <summary>One lookup a subject offers and whether it can be started now. Capability is the integration's declared state, never the last result.</summary>
public sealed record ExpansionOffer(
    ExpansionKind Kind,
    string ProviderId,
    string ProviderName,
    string Capability,
    string? ReasonCode,
    bool CanStart);

public sealed record ExpansionOffers(ExpansionStatus Status, IReadOnlyList<ExpansionOffer> Offers);

/// <summary>
/// The outcome of one lookup. <see cref="Window"/> is null only when the result was obtained but the temporary cache could not
/// hold even its header; <see cref="Header"/> then still states the outcome so nothing is hidden.
/// </summary>
public sealed record ExpansionResult(
    ExpansionStatus Status,
    DiscoveryWindowView? Window,
    ObservedDiscovery? Header,
    TimeSpan RetryAfter = default,
    string? Code = null);

/// <summary>
/// Runs one explicit, owner-scoped source lookup for a server-issued subject, within a server-owned journey allowance, and
/// admits the result as a bounded window context. It reads and writes only the temporary context store: nothing is saved to
/// the catalog, no request, follow, notification or evidence row is touched, and no provider is contacted unless every
/// authorization, scope, capability, rate and budget check has passed.
/// </summary>
/// <remarks>
/// The adapter owns its own provider-work scope and its physical-attempt admission, so this class neither wraps another scope
/// nor calls two operations for one lookup. Discovery is resolved through the same active-provider path as search and detail, on
/// the same registered provider instance, so a disabled or removed source cannot be reached.
/// </remarks>
public sealed class CatalogDiscoveryService(
    CatalogBrowseContextStore store,
    CatalogDiscoveryJourneys journeys,
    CatalogExpansionLimiter limiter,
    IActiveMetadataProviderResolver providers,
    IClock clock,
    TimeProvider timeProvider)
{
    private const string ContextCapacity = "context-capacity";
    private const string ContinuationRefused = "continuation-refused";

    public static DiscoveryOperation OperationOf(ExpansionKind kind) => kind switch
    {
        ExpansionKind.BookSeries => DiscoveryOperation.FindSeriesForBook,
        ExpansionKind.SeriesMembers => DiscoveryOperation.GetSeriesMembers,
        ExpansionKind.ResolveAuthor => DiscoveryOperation.ResolveAuthor,
        ExpansionKind.AuthorWorks => DiscoveryOperation.GetAuthorWorks,
        ExpansionKind.AuthorSeries => DiscoveryOperation.GetAuthorSeries,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Which lookups a subject offers, with the integration's declared capability. No provider call; a settings read only.</summary>
    public async Task<ExpansionOffers> OffersAsync(Guid ownerId, Guid contextId, Guid subjectId, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty || store.TryGetSubject(ownerId, contextId, subjectId, clock.UtcNow) is not { } lookup)
        {
            return new ExpansionOffers(ExpansionStatus.NotFound, []);
        }

        var offers = new List<ExpansionOffer>();
        foreach (var kind in Applicable(lookup))
        {
            var (provider, capability, reason) = await ResolveAsync(lookup.Subject.ProviderId, kind, cancellationToken);
            var name = lookup.Subject.ProviderName;
            offers.Add(new ExpansionOffer(
                kind, lookup.Subject.ProviderId, name,
                capability?.State.ToString() ?? "Unavailable",
                reason ?? (capability is { State: not CapabilityState.Supported } ? capability.ReasonCode : null),
                provider is not null && capability is { State: CapabilityState.Supported or CapabilityState.Unverified }));
        }

        return new ExpansionOffers(ExpansionStatus.Ok, offers);
    }

    /// <summary>Starts one lookup for a subject this owner holds.</summary>
    public async Task<ExpansionResult> ExpandAsync(
        Guid ownerId, Guid contextId, Guid subjectId, ExpansionKind kind, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (ownerId == Guid.Empty || store.TryGetSubject(ownerId, contextId, subjectId, now) is not { } lookup)
        {
            return new ExpansionResult(ExpansionStatus.NotFound, null, null);
        }

        if (!Applicable(lookup).Contains(kind)) return new ExpansionResult(ExpansionStatus.BadScope, null, null);

        var (provider, capability, reason) = await ResolveAsync(lookup.Subject.ProviderId, kind, cancellationToken);
        if (provider is null) return new ExpansionResult(ExpansionStatus.SourceUnavailable, null, null, Code: reason);
        if (capability is null || capability.State == CapabilityState.Unsupported)
        {
            return new ExpansionResult(ExpansionStatus.Unsupported, null, null, Code: capability?.ReasonCode ?? "not-implemented");
        }

        // Resolving the source awaited a settings read, so time has passed. Everything below is decided against a fresh instant and a
        // fresh read of the subject: a context that ended meanwhile is not a subject any more, and nothing is called or charged for it.
        cancellationToken.ThrowIfCancellationRequested();
        now = clock.UtcNow;
        if (store.TryGetSubject(ownerId, contextId, subjectId, now) is not { } current)
        {
            return new ExpansionResult(ExpansionStatus.NotFound, null, null);
        }

        lookup = current;

        // The subject the operation is begun (and any continuation bound) with. Built only from server-held provenance.
        if (!TrySubject(lookup, kind, out var subject)) return new ExpansionResult(ExpansionStatus.BadScope, null, null);

        var journey = lookup.Window is { } parent
            ? journeys.Find(ownerId, parent.JourneyId)
            : journeys.GetOrCreateForRoot(ownerId, lookup.ContextId, lookup.ExpiresAtUtc, now, timeProvider);
        if (journey is null) return new ExpansionResult(ExpansionStatus.JourneyEnded, null, null);

        // Zero-call rejections are all behind us; from here a start is spent and the user's one running slot is held.
        var admission = limiter.TryStart(ownerId, now, out var permit, out var retryAfter);
        if (admission != ExpansionAdmission.Admitted)
        {
            return new ExpansionResult(
                admission == ExpansionAdmission.Busy ? ExpansionStatus.Busy : ExpansionStatus.RateLimited, null, null, retryAfter);
        }

        using (permit)
        {
            var operation = journey.BeginOperation(OperationOf(kind), provider.ProviderId, subject.OperationSubject);
            if (operation is null) return new ExpansionResult(ExpansionStatus.JourneyEnded, null, null);
            return await RunAsync(ownerId, journey, operation, provider, capability, kind, subject, lookup, null, cancellationToken);
        }
    }

    /// <summary>Follows the server-issued continuation of one window. The client names the window only; the token never leaves the host.</summary>
    public async Task<ExpansionResult> ContinueAsync(Guid ownerId, Guid windowContextId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (ownerId == Guid.Empty || store.TryGetWindow(ownerId, windowContextId, now) is not { } window)
        {
            return new ExpansionResult(ExpansionStatus.NotFound, null, null);
        }

        var header = window.Header;
        if (header.ContinuationToken is null || !Enum.TryParse<DiscoveryOperation>(header.Operation, out var operationKind) ||
            KindOf(operationKind) is not { } kind || header.SubjectNativeKind is null || header.SubjectNativeId is null ||
            !SourceRef.TryCreate(header.ProviderId, header.SubjectNativeId, header.SubjectNativeKind.Value, out var operationSubject))
        {
            return new ExpansionResult(ExpansionStatus.NoContinuation, null, null);
        }

        var (provider, capability, reason) = await ResolveAsync(header.ProviderId, kind, cancellationToken);
        if (provider is null) return new ExpansionResult(ExpansionStatus.SourceUnavailable, null, null, Code: reason);
        if (capability is null || capability.State == CapabilityState.Unsupported)
        {
            return new ExpansionResult(ExpansionStatus.Unsupported, null, null, Code: capability?.ReasonCode ?? "not-implemented");
        }

        // The settings read took time: the window and its journey are read again against a fresh instant before anything is redeemed.
        cancellationToken.ThrowIfCancellationRequested();
        now = clock.UtcNow;
        if (store.TryGetWindow(ownerId, windowContextId, now) is not { } liveWindow)
        {
            return new ExpansionResult(ExpansionStatus.NotFound, null, null);
        }

        window = liveWindow;
        var journey = journeys.Find(ownerId, header.JourneyId);
        if (journey is null) return new ExpansionResult(ExpansionStatus.JourneyEnded, null, null);

        // Foreign, altered, expired, wrong-kind and cross-operation tokens are all refused here without a provider call.
        var redeemed = journey.Redeem(header.ContinuationToken, ownerId, operationKind, header.ProviderId, operationSubject);
        if (redeemed.Status != ContinuationStatus.Accepted || redeemed.Operation is null || redeemed.Cursor is null)
        {
            return new ExpansionResult(ExpansionStatus.NoContinuation, null, null);
        }

        var admission = limiter.TryStart(ownerId, now, out var permit, out var retryAfter);
        if (admission != ExpansionAdmission.Admitted)
        {
            return new ExpansionResult(
                admission == ExpansionAdmission.Busy ? ExpansionStatus.Busy : ExpansionStatus.RateLimited, null, null, retryAfter);
        }

        using (permit)
        {
            var subject = new LookupSubject(
                header.SubjectLabel, header.SubjectNativeKind.Value, header.SubjectNativeId, operationSubject!, null);
            // The next page keeps the original lookup's subject and parent, so a person can always return to where they started.
            var parent = new SubjectLookup(
                header.ParentContextId,
                new ObservedSubject(header.ParentSubjectId, header.SubjectKind, header.ProviderId, header.ProviderName, header.SubjectLabel, header.SubjectNativeId, 0),
                null, header, window.ExpiresAtUtc, false);
            return await RunAsync(ownerId, journey, redeemed.Operation, provider, capability, kind, subject, parent, redeemed.Cursor, cancellationToken);
        }
    }

    // ------------------------------------------------------------------------------------------------ the lookup itself

    private async Task<ExpansionResult> RunAsync(
        Guid ownerId,
        DiscoveryJourney journey,
        DiscoveryOperationBudget operation,
        IBookMetadataDiscoveryProvider provider,
        DiscoveryCapability capability,
        ExpansionKind kind,
        LookupSubject subject,
        SubjectLookup lookup,
        DiscoveryCursor? cursor,
        CancellationToken cancellationToken)
    {
        var providerName = lookup.Subject.ProviderName;
        DiscoveryWindowInput input;
        switch (kind)
        {
            case ExpansionKind.BookSeries:
            {
                var result = await provider.FindSeriesClaimsForBookAsync(subject.OperationSubject, operation, cancellationToken);
                input = Build(result, ToBooks(subject, result), [], kind, journey, operation, capability, subject, lookup, providerName);
                break;
            }

            case ExpansionKind.SeriesMembers:
            {
                var series = new SeriesRef(subject.Label, subject.OperationSubject);
                var result = await provider.GetSeriesMembersAsync(series, cursor, operation, cancellationToken);
                input = Build(result, result.Items.Select(member => ToBook(member, subject.Label)).ToArray(), [], kind, journey, operation, capability, subject, lookup, providerName);
                break;
            }

            case ExpansionKind.ResolveAuthor:
            {
                var anchor = AuthorAnchor.FromNameAndBooks(subject.Label, [AnchorBook.FromSource(subject.AnchorBook!)]);
                var result = await provider.ResolveAuthorAsync(anchor, operation, cancellationToken);
                input = Build(result, [], result.Items.Select(ToRef).ToArray(), kind, journey, operation, capability, subject, lookup, providerName);
                break;
            }

            case ExpansionKind.AuthorWorks:
            {
                var author = new AuthorRef(subject.Label, subject.OperationSubject);
                var result = await provider.GetAuthorWorksAsync(author, cursor, operation, cancellationToken);
                input = Build(result, result.Items.Select(ToBook).ToArray(), [], kind, journey, operation, capability, subject, lookup, providerName);
                break;
            }

            default:
            {
                var author = new AuthorRef(subject.Label, subject.OperationSubject);
                var result = await provider.GetAuthorSeriesAsync(author, cursor, operation, cancellationToken);
                input = Build(result, [], result.Items.Select(ToRef).ToArray(), kind, journey, operation, capability, subject, lookup, providerName);
                break;
            }
        }

        var now = clock.UtcNow;
        // The window expires at the instant of the context its subject lives in (which itself inherited the root's): it never refreshes it.
        var lease = store.AdmitDiscoveryWindow(ownerId, input, lookup.ExpiresAtUtc, now);
        if (lease is null)
        {
            var refused = input.Header with { ItemsRetained = 0, Notes = [.. input.Header.Notes, ContextCapacity], ContinuationToken = null };
            return new ExpansionResult(ExpansionStatus.Ok, null, refused);
        }

        return new ExpansionResult(ExpansionStatus.Ok, store.TryGetWindow(ownerId, lease.ContextId, now), null);
    }

    private static DiscoveryWindowInput Build<T>(
        DiscoveryResult<T> result,
        IReadOnlyList<WindowBook> books,
        IReadOnlyList<WindowRef> refs,
        ExpansionKind kind,
        DiscoveryJourney journey,
        DiscoveryOperationBudget operation,
        DiscoveryCapability capability,
        LookupSubject subject,
        SubjectLookup lookup,
        string providerName)
    {
        var notes = result.Notes.ToList();
        string? token = null;
        if (result.Completion == DiscoveryCompletion.MoreAvailable && result.Next is not null)
        {
            var issue = journey.IssueContinuation(operation, result.Next);
            if (issue.Status == ContinuationIssueStatus.Issued) token = issue.Token;
            else notes.Add(ContinuationRefused);
        }

        // The token is provisional: the store withholds it if it cannot retain every item, because the dropped items would be skipped.
        var header = new ObservedDiscovery(
            journey.JourneyId, OperationOf(kind).ToString(), lookup.Subject.ProviderId, providerName,
            result.Outcome.ToString(), result.Completion.ToString(), notes, capability.State.ToString(), result.Scope.Filter,
            lookup.ContextId, lookup.Subject.SubjectId, lookup.Subject.Kind, subject.Label,
            result.Usage.Attempts, result.Usage.Retries, result.Usage.Pages, 0, 0, result.ObservedAtUtc)
        {
            SubjectNativeKind = subject.OperationSubject.Kind,
            SubjectNativeId = subject.OperationSubject.ExternalId,
            ContinuationToken = token
        };
        return new DiscoveryWindowInput(header, providerName, books, refs);
    }

    // ------------------------------------------------------------------------------------------------ mapping

    private static IReadOnlyList<WindowBook> ToBooks(LookupSubject subject, DiscoveryResult<BookSeriesClaim> result)
    {
        if (result.Items.Count == 0) return [];
        // The claims describe one book: the subject itself, with each claim as its own membership so a conflict stays visible.
        var memberships = result.Items.Select(claim => new WindowMembership(
            claim.Series.Label,
            claim.Series.Id?.ExternalId,
            claim.Position?.Label,
            claim.Position?.Sort,
            "native",
            claim.Series.Canonical is { State: not CanonicalLinkState.NotStated } link ? link.State.ToString() : null,
            claim.Series.Canonical?.Target?.ExternalId)).ToArray();
        return
        [
            new WindowBook(subject.OperationSubject, subject.Label, null, null, null, [], memberships, null, null, null)
        ];
    }

    private static WindowBook ToBook(SeriesMember member, string seriesLabel)
    {
        var positions = member.Positions.Count == 0 ? [null] : member.Positions.Cast<PositionClaim?>().ToArray();
        var memberships = positions.Select(position => new WindowMembership(
            seriesLabel, member.Series?.ExternalId, position?.Label, position?.Sort, "native", null, null)).ToArray();
        return ToBook(member.Book, memberships);
    }

    private static WindowBook ToBook(AuthorWork work)
    {
        var memberships = work.Series.Select(claim => new WindowMembership(
            claim.Series.Label,
            claim.Series.Id?.ExternalId,
            claim.Position?.Label,
            claim.Position?.Sort,
            "native",
            claim.Series.Canonical is { State: not CanonicalLinkState.NotStated } link ? link.State.ToString() : null,
            claim.Series.Canonical?.Target?.ExternalId)).ToArray();
        return ToBook(work.Book, memberships);
    }

    private static WindowBook ToBook(DiscoveredBook book, IReadOnlyList<WindowMembership> memberships)
    {
        var publication = book.Publication;
        return new WindowBook(
            book.Source,
            // The reported title is what a person sees; a normalised form is evidence for matching, never a replacement.
            book.Title.Reported,
            publication.State == PublicationDateState.Dated ? publication.Date : null,
            publication.State == PublicationDateState.Dated ? publication.Precision : null,
            publication.State == PublicationDateState.Dated ? publication.Basis : null,
            book.Credits.Select(credit => new WindowCredit(credit.Ordinal, credit.Label, credit.AuthorId?.ExternalId, credit.Role)).ToArray(),
            memberships,
            book.Version?.Kind,
            book.Version?.Label,
            book.Format);
    }

    private static WindowRef ToRef(AuthorRef author) => new(
        ObservedSubjectKind.Author, author.Name, author.Id.ExternalId, "native", author.Disambiguation, author.WorkCount,
        null, null, null, null);

    private static WindowRef ToRef(AuthorSeriesEntry entry) => new(
        ObservedSubjectKind.Series, entry.Series.Label, entry.Series.Id?.ExternalId,
        entry.Relationship.Basis == RelationshipBasis.Native ? "native" : "derived:" + entry.Relationship.DerivedScope,
        null, null, entry.Series.IsCompleted, entry.Series.PrimaryBooksCount,
        entry.Series.Canonical is { State: not CanonicalLinkState.NotStated } link ? link.State.ToString() : null,
        entry.Series.Canonical?.Target?.ExternalId);

    // ------------------------------------------------------------------------------------------------ eligibility

    private sealed record LookupSubject(string Label, ExternalReferenceEntityType NativeKind, string NativeId, SourceRef OperationSubject, SourceRef? AnchorBook);

    private static ExpansionKind? KindOf(DiscoveryOperation operation) => operation switch
    {
        DiscoveryOperation.FindSeriesForBook => ExpansionKind.BookSeries,
        DiscoveryOperation.GetSeriesMembers => ExpansionKind.SeriesMembers,
        DiscoveryOperation.ResolveAuthor => ExpansionKind.ResolveAuthor,
        DiscoveryOperation.GetAuthorWorks => ExpansionKind.AuthorWorks,
        DiscoveryOperation.GetAuthorSeries => ExpansionKind.AuthorSeries,
        _ => null
    };

    private static IReadOnlyList<ExpansionKind> Applicable(SubjectLookup lookup)
    {
        var subject = lookup.Subject;
        return subject.Kind switch
        {
            ObservedSubjectKind.Book when subject.NativeKind is not null && subject.NativeExternalId is not null => [ExpansionKind.BookSeries],
            ObservedSubjectKind.Series when subject.NativeKind == ExternalReferenceEntityType.Series && subject.NativeExternalId is not null => [ExpansionKind.SeriesMembers],
            ObservedSubjectKind.Author when subject.NativeKind == ExternalReferenceEntityType.Author && subject.NativeExternalId is not null =>
                [ExpansionKind.AuthorWorks, ExpansionKind.AuthorSeries],
            // No native id: the name can only be put to the source together with a book the source itself credited it on.
            ObservedSubjectKind.Author when lookup.AnchorRecord is { SourceKind: not null } && !string.IsNullOrWhiteSpace(subject.Label) =>
                [ExpansionKind.ResolveAuthor],
            _ => []
        };
    }

    private static bool TrySubject(SubjectLookup lookup, ExpansionKind kind, out LookupSubject subject)
    {
        subject = null!;
        var native = lookup.Subject;
        var provider = native.ProviderId;
        if (kind == ExpansionKind.ResolveAuthor)
        {
            if (lookup.AnchorRecord?.SourceKind is not { } anchorKind ||
                !SourceRef.TryCreate(provider, lookup.AnchorRecord.ExternalId, anchorKind, out var book))
            {
                return false;
            }

            subject = new LookupSubject(native.Label, anchorKind, book!.ExternalId, book, book);
            return true;
        }

        if (native.NativeKind is not { } nativeKind || native.NativeExternalId is null ||
            !SourceRef.TryCreate(provider, native.NativeExternalId, nativeKind, out var reference))
        {
            return false;
        }

        subject = new LookupSubject(native.Label, nativeKind, native.NativeExternalId, reference!, null);
        return true;
    }

    private async Task<(IBookMetadataDiscoveryProvider? Provider, DiscoveryCapability? Capability, string? Reason)> ResolveAsync(
        string providerId, ExpansionKind kind, CancellationToken cancellationToken)
    {
        var active = await providers.FindActiveProviderAsync(providerId, cancellationToken);
        if (active is null)
        {
            return (null, null, providers.IsRegistered(providerId) ? "source-disabled" : "source-removed");
        }

        if (active is not IBookMetadataDiscoveryProvider discovery) return (null, null, "not-implemented");
        return (discovery, discovery.Capabilities.Get(OperationOf(kind)), null);
    }
}
