namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// Optional, read-only author/series discovery that a metadata integration may add beside <see cref="IBookMetadataProvider"/>.
/// An integration that does not implement it has not implemented discovery; that is not proof the upstream API cannot.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must make no write, persist nothing, and spend only what <see cref="IDiscoveryBudget"/> admits: every
/// physical attempt, retry and ancillary lookup is reserved before it is dispatched. A caller's cancellation must surface
/// as <see cref="OperationCanceledException"/>; it is never converted into an empty or successful result. An internal
/// deadline may return a qualified <see cref="DiscoveryOutcome.TimedOut"/> result holding what was already observed.
/// </para>
/// <para>
/// Identity: a native id is used as given; a name never selects an author (see <see cref="AuthorResolutionPolicy"/>);
/// a derived edge may target a native series but never turns a label into a native id.
/// </para>
/// </remarks>
public interface IBookMetadataDiscoveryProvider
{
    string ProviderId { get; }

    /// <summary>What this integration implements. Runtime availability (disabled, no key) is decided by the host, not here.</summary>
    DiscoveryCapabilities Capabilities { get; }

    /// <summary>The series a single source book is stated to belong to, one entry per series claim.</summary>
    Task<DiscoveryResult<SeriesRef>> FindSeriesForBookAsync(
        SourceRef book, IDiscoveryBudget budget, CancellationToken cancellationToken);

    /// <summary>One page of a series' members, selected by the series' native id. Never a name search.</summary>
    Task<DiscoveryResult<SeriesMember>> GetSeriesMembersAsync(
        SeriesRef series, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken);

    /// <summary>Resolves an anchor to an author, or returns choices. See <see cref="AuthorResolutionPolicy"/>.</summary>
    Task<DiscoveryResult<AuthorRef>> ResolveAuthorAsync(
        AuthorAnchor anchor, IDiscoveryBudget budget, CancellationToken cancellationToken);

    /// <summary>One page of an author's works, selected by the author's native id.</summary>
    Task<DiscoveryResult<AuthorWork>> GetAuthorWorksAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken);

    /// <summary>Series reached from an author. Each entry says whether that edge is native or derived and over what scope.</summary>
    Task<DiscoveryResult<AuthorSeriesEntry>> GetAuthorSeriesAsync(
        AuthorRef author, DiscoveryCursor? cursor, IDiscoveryBudget budget, CancellationToken cancellationToken);
}

/// <summary>A provider's author candidate and whether a supplied anchor corroborates it. Counts are shown, never used.</summary>
public sealed record AuthorCandidate(AuthorRef Author, bool NameMatchesExactly, bool CorroboratedByAnchor);

/// <summary>
/// The one shared rule for choosing an author, so no adapter invents its own. A popularity or work-count winner is never
/// selected: an exact name is not identity (a namesake educationist outranks the novelist by count).
/// </summary>
public static class AuthorResolutionPolicy
{
    /// <summary>
    /// <see cref="DiscoveryOutcome.Ok"/> only when exactly one candidate is established by the anchor: the supplied native id,
    /// or a name match corroborated by an anchor book. Every other case is choices, or empty when nothing fits.
    /// </summary>
    public static DiscoveryResult<AuthorRef> Decide(
        AuthorAnchor anchor,
        IReadOnlyList<AuthorCandidate> candidates,
        DiscoveryScope scope,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string>? extraNotes = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(candidates);
        var carried = extraNotes ?? [];

        if (anchor.Kind == AuthorAnchorKind.SourceId)
        {
            var exact = candidates.Where(candidate => candidate.Author.Id == anchor.Id).ToArray();
            return exact.Length == 1
                ? DiscoveryResult.Found<AuthorRef>([exact[0].Author], scope, DiscoveryCompletion.ExhaustedInCheckedScope, observedAtUtc, usage, notes: carried)
                : DiscoveryResult.Empty<AuthorRef>(scope, observedAtUtc, usage, carried);
        }

        var named = candidates.Where(candidate => candidate.NameMatchesExactly).ToArray();
        if (named.Length == 0)
        {
            return DiscoveryResult.Empty<AuthorRef>(scope, observedAtUtc, usage, carried);
        }

        if (anchor.Kind == AuthorAnchorKind.NameWithBooks)
        {
            var corroborated = named.Where(candidate => candidate.CorroboratedByAnchor).ToArray();
            if (corroborated.Length == 1)
            {
                return DiscoveryResult.Found<AuthorRef>(
                    [corroborated[0].Author], scope, DiscoveryCompletion.ExhaustedInCheckedScope, observedAtUtc, usage, notes: carried);
            }
        }

        return DiscoveryResult.Choices<AuthorRef>(
            named.Select(candidate => candidate.Author).ToArray(), scope, observedAtUtc, usage, [.. carried, DiscoveryCodes.AnchorInsufficient]);
    }
}
