
namespace FamilyLibrarian.Application.Catalog.Observation;

/// <summary>What one observation of a provider record did. The names are the accepted outcome state machine, never a bare boolean.</summary>
public enum ObservationKind
{
    /// <summary>The reference was already saved: the Work is returned, with no provider call and no write.</summary>
    Existing,

    /// <summary>A new Work was created.</summary>
    Created,

    /// <summary>The record was attached to an existing Work on validated ISBN proof, adding new evidence.</summary>
    Attached,

    /// <summary>A known reference's fresh record added permitted new evidence.</summary>
    Refreshed,

    /// <summary>A known reference's fresh record changed nothing; only last-seen bookkeeping moved.</summary>
    Unchanged,

    /// <summary>A known reference's fresh record contradicts its Work; only a bounded evidence snapshot was kept.</summary>
    Contradiction,

    /// <summary>The only overlap with a saved Work is an excluded edition: nothing attached.</summary>
    NoProof,

    /// <summary>The proof was contested (several Works, or a gate failed): nothing attached.</summary>
    Collision,

    /// <summary>A plausible record with no ISBN proof: reported, never persisted or attached.</summary>
    Tentative,

    /// <summary>The Work was retired, or no longer holds the reference, after the provider was asked.</summary>
    WorkRetired
}

/// <summary>Why a Resolve created a separate Work instead of attaching: the evidence that stopped the attachment.</summary>
/// <param name="Kind"><see cref="ObservationKind.Collision"/> or <see cref="ObservationKind.NoProof"/>.</param>
/// <param name="Reason">The typed reason, for example <c>MultipleWorks</c>, <c>Author</c>, <c>Language</c> or <c>ExcludedEditionOverlap</c>.</param>
public sealed record ObservationDetail(ObservationKind Kind, string Reason);

public enum ProviderRefreshStatus
{
    Refreshed,
    Unchanged,
    Attached,
    Contradiction,
    Collision,
    NoProof,
    Tentative,

    /// <summary>The provider no longer returns the record. Absence is staleness, never a deletion.</summary>
    NotFound,

    /// <summary>The provider failed, timed out or returned an unreadable record. Nothing was written for it.</summary>
    ProviderUnavailable,

    /// <summary>A stored reference's provider is switched off or no longer installed. Nothing was asked, nothing was removed.</summary>
    ProviderNotActive,

    /// <summary>The Work was retired, or no longer holds the reference, before the write.</summary>
    WorkRetired,

    /// <summary>The catalog write lock was not available in time. Nothing was written for it.</summary>
    Busy,

    /// <summary>A concurrent writer contested the rows twice. Nothing was written for it.</summary>
    Conflict,

    /// <summary>The commit's outcome could not be established; the change may exist.</summary>
    OutcomeUnknown,

    /// <summary>The catalog database could not be reached before anything could commit. Nothing was written for it.</summary>
    CatalogUnavailable,

    /// <summary>The overall deadline ended the refresh before this provider was reached.</summary>
    DeadlineReached,

    /// <summary>The search found nothing that could be a record of this Work.</summary>
    NoCandidate
}

/// <param name="ProviderId">The provider the step asked or skipped.</param>
/// <param name="ExternalId">The record the step concerned, when there was one.</param>
/// <param name="Phase"><c>Refresh</c> for a stored reference, <c>Discover</c> for a provider with none.</param>
/// <param name="Reason">A typed reason where the status has one (a gate that failed, a contradicted edition).</param>
/// <param name="ContradictedIsbns">Persisted editions this record now contradicts: reported, never moved or deleted.</param>
public sealed record ProviderRefreshResult(
    string ProviderId,
    string? ExternalId,
    string Phase,
    ProviderRefreshStatus Status,
    string? Reason = null,
    IReadOnlyList<string>? ContradictedIsbns = null);

/// <summary>A plausible record of the Work from a source with no ISBN proof. Never persisted and never attached.</summary>
public sealed record TentativeProposal(string ProviderId, string ExternalId, string Title, IReadOnlyList<string> Authors, string Reason);

public enum WorkRefreshStatus
{
    /// <summary>Every step ran to its own conclusion (a step may still have found nothing).</summary>
    Completed,

    /// <summary>Some step failed, timed out or was skipped; the facts that did commit are kept and reported.</summary>
    Partial,

    WorkNotFound,
    WorkRetired
}

public sealed record WorkRefreshReport(
    Guid WorkId,
    WorkRefreshStatus Status,
    IReadOnlyList<ProviderRefreshResult> Providers,
    IReadOnlyList<TentativeProposal> Proposals);
