using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Resolves a provider record to a catalog Work for the callers that have always asked for one (the request flow, the book page, the
/// conversation). It owns no catalog-writing logic of its own: every resolution is the single observation path's ordinary
/// <c>Resolve</c> intent, so identity rules, evidence and write coordination exist exactly once.
/// </summary>
public sealed class CatalogWorkResolver(
    IActiveMetadataProviderResolver providers,
    ICatalogRepository catalogRepository,
    ICatalogWriteScope writeScope,
    NotificationService notifications,
    IClock clock,
    ICatalogResolutionDiagnostics? diagnostics = null)
{
    private readonly CatalogObservationService observation =
        new(providers, catalogRepository, writeScope, notifications, clock, diagnostics);

    /// <summary>
    /// Resolves a provider record to a catalog Work. A reference that is already saved is returned with no provider call and no
    /// write. Otherwise only an active provider is asked, once, outside any transaction; the catalog is then changed inside the
    /// catalog write scope, which serializes writers across processes and retries a recoverable conflict at most once. A record
    /// whose proof is contested creates a distinct Work (see <see cref="CatalogWorkResolution.Detail"/>); it is never attached.
    /// </summary>
    /// <exception cref="CatalogResolutionException">
    /// An expected, typed failure. For every fault except <see cref="CatalogResolutionFault.OutcomeUnknown"/> nothing was written.
    /// <c>OutcomeUnknown</c> means the commit's outcome could not be established, so the change may well exist: the caller must
    /// check before repeating it.
    /// </exception>
    public Task<CatalogWorkResolution> ResolveAsync(string providerId, string externalId, CancellationToken cancellationToken) =>
        observation.ResolveAsync(providerId, externalId, cancellationToken);

    public Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken) =>
        catalogRepository.GetWorkAsync(workId, cancellationToken);

    public Task<IReadOnlyList<ExternalReference>> GetWorkSourcesAsync(
        Guid workId,
        CancellationToken cancellationToken) =>
        catalogRepository.GetWorkSourcesAsync(workId, cancellationToken);
}

public enum CatalogResolutionOutcome
{
    /// <summary>The Work was already saved; nothing was written.</summary>
    Existing,

    /// <summary>A new Work was created.</summary>
    Created,

    /// <summary>The provider reference was added to an existing Work that its validated ISBN proof names.</summary>
    Attached
}

/// <param name="Work">The resolved Work. Loaded with its authors, editions and series entries; safe to read after the write scope ended.</param>
/// <param name="WasCreated">True only for the attempt that committed a new Work.</param>
public sealed record CatalogWorkResolution(Work Work, bool WasCreated)
{
    public CatalogResolutionOutcome Outcome { get; init; } = CatalogResolutionOutcome.Existing;

    /// <summary>Credits and memberships that could not be bound to an existing record, for the caller and tests.</summary>
    public CatalogResolutionDiagnostics Diagnostics { get; init; } = CatalogResolutionDiagnostics.None;

    /// <summary>
    /// Why a created Work is separate from a saved Work the record overlapped (a contested ISBN set, a failed identity check, or
    /// an overlap only on an excluded edition). Null for every other result.
    /// </summary>
    public ObservationDetail? Detail { get; init; }
}

public sealed record CatalogResolutionDiagnostics(
    IReadOnlyList<AmbiguousAuthorCredit> AmbiguousAuthors,
    IReadOnlyList<OmittedSeriesMembership> OmittedSeriesMemberships)
{
    public static CatalogResolutionDiagnostics None { get; } = new([], []);

    public bool IsEmpty => AmbiguousAuthors.Count == 0 && OmittedSeriesMemberships.Count == 0;
}

/// <summary>An author credit that matched several existing Authors by name, so it received its own provisional Author instead.</summary>
public sealed record AmbiguousAuthorCredit(string ReportedName, Guid NewAuthorId, IReadOnlyList<Guid> NamesakeAuthorIds);

/// <summary>A series membership that matched several existing Series by name, so it was kept as an unbound claim, not an entry.</summary>
public sealed record OmittedSeriesMembership(string ReportedName, IReadOnlyList<Guid> NamesakeSeriesIds);

/// <summary>Receives what a committed creation could not bind, so it can be logged without the Application layer owning a logger.</summary>
public interface ICatalogResolutionDiagnostics
{
    void Report(Guid workId, CatalogResolutionDiagnostics diagnostics);
}

public enum CatalogResolutionFault
{
    /// <summary>No installed provider has this id (HTTP 404).</summary>
    UnknownProvider,

    /// <summary>The provider is installed but switched off, so it was not asked (HTTP 409).</summary>
    ProviderNotEnabled,

    /// <summary>The provider has no such record (HTTP 404).</summary>
    CandidateNotFound,

    /// <summary>A concurrent writer contested the rows and no safe completion exists; nothing was written (HTTP 409).</summary>
    Conflict,

    /// <summary>The catalog write lock was not available in time; nothing was written (HTTP 503, retryable).</summary>
    Busy,

    /// <summary>The database could not be reached before anything could commit (HTTP 503, retryable).</summary>
    Unavailable,

    /// <summary>The commit's outcome could not be established; the change may or may not exist (HTTP 503, retryable).</summary>
    OutcomeUnknown
}

/// <summary>An expected, deliberately mapped resolution failure. Programming and storage faults are not wrapped in this type.</summary>
public sealed class CatalogResolutionException(CatalogResolutionFault fault, string message) : InvalidOperationException(message)
{
    public CatalogResolutionFault Fault { get; } = fault;
}
