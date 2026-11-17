using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// The catalog rows one write attempt may read and change. Every member is bound to the attempt's own unit of work, so a
/// retry can never reuse a tracked entity from an abandoned transaction.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ICatalogRepository"/>: that interface serves readers, followers and gap detection with
/// loaded graphs, while this one is the narrow, list-based surface a writer needs. A lookup that can legitimately match several
/// rows returns all of them so the writer decides, instead of throwing or silently preferring one.
/// </remarks>
public interface ICatalogMutationRepository
{
    Task<Work?> FindWorkByExternalReferenceAsync(string providerId, string externalId, CancellationToken cancellationToken);

    /// <summary>Every non-retired Work holding at least one of the ISBN-13s, in a stable order. Empty when none does.</summary>
    Task<IReadOnlyList<Work>> ListWorksByIsbn13Async(IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken);

    /// <summary>Every Author with this normalized name, oldest first. A name alone is not an identity, so several are possible.</summary>
    Task<IReadOnlyList<Author>> ListAuthorsByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken);

    /// <summary>Every Series with this normalized name, oldest first.</summary>
    Task<IReadOnlyList<Series>> ListSeriesByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken);

    /// <summary>The Work with its authors, editions and series entries, retired or not, tracked by this attempt's unit of work.</summary>
    Task<Work?> GetWorkAsync(Guid workId, CancellationToken cancellationToken);

    /// <summary>Every Work-type provider reference of the Work, in a stable order.</summary>
    Task<IReadOnlyList<ExternalReference>> ListWorkReferencesAsync(Guid workId, CancellationToken cancellationToken);

    /// <summary>For each of the ISBN-13s that a non-retired Work holds, that Work's id (the unique index allows one owner).</summary>
    Task<IReadOnlyDictionary<string, Guid>> MapIsbnOwnersAsync(IReadOnlyCollection<string> isbn13s, CancellationToken cancellationToken);

    /// <summary>
    /// Every claim of the Work that belongs to one of the given facts, tracked and complete. A writer decides supersession, selection
    /// and compaction from what this returns, so it is never a prefix or a capped page: a fact's rows are bounded by retention (at
    /// most eleven per source key), and a bounded inspection read is a different method on a different interface.
    /// </summary>
    Task<IReadOnlyList<MetadataClaim>> ListClaimsAsync(Guid workId, IReadOnlyCollection<ClaimGroup> groups, CancellationToken cancellationToken);

    /// <summary>Every snapshot of one source record of the Work, oldest first, tracked. Retention keeps a source record's snapshots to a handful.</summary>
    Task<IReadOnlyList<SourceObservation>> ListObservationsAsync(
        Guid workId, string providerId, string externalId, CancellationToken cancellationToken);

    /// <summary>
    /// An opaque revision of one source record's stored evidence: its snapshots and every claim it stated, across all facts. It is
    /// built from what the database itself assigns to each row (the row identity and the version PostgreSQL stamps on every insert,
    /// update and supersession), never from a time the application read, so two different states of the evidence never share a
    /// revision however a clock behaves. A row inserted, changed or removed by any transaction changes it; the same rows left alone
    /// do not. Compared only for equality, and only between reads made under the catalog lock.
    /// </summary>
    Task<string> GetSourceRevisionAsync(Guid workId, string providerId, string externalId, CancellationToken cancellationToken);

    /// <summary>The ids of every Author or Series the provider's own id is recorded against (a provider-id lookup, never a name one).</summary>
    Task<IReadOnlyList<Guid>> ListEntityIdsByExternalReferenceAsync(
        ExternalReferenceEntityType entityType, string providerId, string externalId, CancellationToken cancellationToken);

    /// <summary>Of the given entities, those already tied to a different id from the same provider.</summary>
    Task<IReadOnlySet<Guid>> ListEntityIdsReferencedByOtherIdAsync(
        ExternalReferenceEntityType entityType,
        string providerId,
        string externalId,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Author>> GetAuthorsAsync(IReadOnlyCollection<Guid> authorIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<Series>> GetSeriesAsync(IReadOnlyCollection<Guid> seriesIds, CancellationToken cancellationToken);

    void AddWork(Work work);

    void AddAuthor(Author author);

    void AddSeries(Series series);

    void AddExternalReference(ExternalReference externalReference);

    /// <summary>Explicitly adds a new Edition to a tracked Work (a client-assigned key must be added, not discovered).</summary>
    void AddEdition(Edition edition);

    void AddSeriesEntry(SeriesEntry seriesEntry);

    void AddClaim(MetadataClaim claim);

    void RemoveClaim(MetadataClaim claim);

    void AddObservation(SourceObservation observation);

    void RemoveObservation(SourceObservation observation);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>One fact of a Work whose claims compete: a field in a scope (the Work, a series id, an ISBN-13 or an unbound label).</summary>
public readonly record struct ClaimGroup(MetadataClaimField Field, string ScopeKey);

/// <summary>
/// Thrown by a verification callback when, after a commit of unknown outcome, the catalog can show neither that the attempt's effects
/// are present nor that they are absent (later writers have changed or compacted what would prove it). The write scope reports the
/// operation's outcome as unknown instead of replaying the attempt over later changes.
/// </summary>
public sealed class CatalogOutcomeUnprovableException : Exception
{
    public CatalogOutcomeUnprovableException()
    {
    }

    public CatalogOutcomeUnprovableException(string message)
        : base(message)
    {
    }

    public CatalogOutcomeUnprovableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Why a catalog write attempt is being repeated, so the mutation can refuse to treat a contested row as won.</summary>
public enum CatalogWriteRetryCause
{
    /// <summary>The first attempt of the operation.</summary>
    None,

    /// <summary>The previous attempt lost a unique-index race with a writer that did not take the catalog lock.</summary>
    UniqueViolation,

    /// <summary>The previous attempt hit a serialization failure, deadlock or optimistic-concurrency conflict.</summary>
    ConcurrentUpdate,

    /// <summary>The previous attempt's commit outcome was unknown and verified as not having happened.</summary>
    UncertainCommit
}

/// <summary>What the database itself records about an abandoned attempt's own transaction, once its outcome was unknown.</summary>
public enum CatalogTransactionStatus
{
    /// <summary>Not established: not a verification session, or the database no longer retains the transaction's status.</summary>
    Unknown = 0,

    /// <summary>The attempt never wrote anything, so no transaction was ever recorded and there is nothing to prove.</summary>
    NoWrites,

    /// <summary>The database records the attempt's transaction as committed: every effect it wrote is in the catalog (or has since been changed by later writers).</summary>
    Committed,

    /// <summary>The database records the attempt's transaction as rolled back: none of its effects exist.</summary>
    RolledBack
}

/// <summary>One catalog write attempt: its fresh repositories and why it is running.</summary>
public interface ICatalogWriteSession
{
    ICatalogMutationRepository Catalog { get; }

    /// <summary>Reads follower rows through the same unit of work as <see cref="Catalog"/>.</summary>
    IFollowRepository Follows { get; }

    CatalogWriteRetryCause RetryCause { get; }

    /// <summary>
    /// In a verification session, the database's own record of the abandoned attempt's transaction (its transaction id, not any time
    /// the application noted), read under the same lock. In an ordinary attempt it is <see cref="CatalogTransactionStatus.Unknown"/>.
    /// </summary>
    CatalogTransactionStatus AttemptedTransaction => CatalogTransactionStatus.Unknown;
}

public enum CatalogWriteStatus
{
    /// <summary>The attempt committed.</summary>
    Committed,

    /// <summary>The commit's outcome was unknown, and a verification read proved the operation's effects are present.</summary>
    Recovered,

    /// <summary>The catalog write lock could not be acquired within its bound. Nothing was written.</summary>
    Busy,

    /// <summary>A conflicting writer won the operation's rows twice. Nothing was written.</summary>
    Conflict,

    /// <summary>The database could not be reached, or the commit outcome could not be established. Nothing is claimed about whether it was applied.</summary>
    OutcomeUnknown,

    /// <summary>The database could not be reached before anything could have been committed. Nothing was written.</summary>
    Unavailable,

    /// <summary>The caller's overall deadline ended the wait or the attempt before it committed. The attempt was rolled back: nothing was written.</summary>
    DeadlineReached
}

public sealed record CatalogWriteResult<T>(CatalogWriteStatus Status, T? Value) where T : class;

/// <summary>
/// Runs a catalog mutation so it is serialized against every other cooperating catalog writer across all processes.
/// </summary>
/// <remarks>
/// Each attempt gets its own unit of work, one transaction, and the catalog-wide transaction-scoped lock; the mutation re-reads
/// whatever it depends on after the lock is held. A recoverable conflict is retried at most once with a new unit of work. Provider
/// calls never happen inside this scope. The lock only coordinates writers that use this scope (the resolver and orphan
/// retirement); the database's unique indexes remain the backstop for anything else.
/// </remarks>
public interface ICatalogWriteScope
{
    /// <param name="mutation">Reads, adds and saves through the supplied session only; it returns the operation's result.</param>
    /// <param name="verifyAfterUnknownCommit">
    /// Called with a new session, holding the same lock, when a commit's outcome is unknown. It receives the abandoned attempt's
    /// result and returns the committed equivalent when the operation's effects are demonstrably present, or null when they are
    /// demonstrably absent. When neither can be shown it throws <see cref="CatalogOutcomeUnprovableException"/>. The session's
    /// <see cref="ICatalogWriteSession.AttemptedTransaction"/> is the database's own record of whether the abandoned transaction
    /// committed; a verification must rest on that, and on database-stamped row revisions, never on clock readings.
    /// </param>
    /// <param name="cancellationToken">The caller's own cancellation. It is honoured everywhere, including a commit in flight, and surfaces as cancellation.</param>
    /// <param name="deadline">
    /// An overall time budget the caller owns. It ends the wait for the lock, the attempt's reads and writes and any verification, but
    /// it never interrupts a commit already sent: an attempt that is cut off before its commit is rolled back and reported as
    /// <see cref="CatalogWriteStatus.DeadlineReached"/>, and a verification cut off by it is reported as
    /// <see cref="CatalogWriteStatus.OutcomeUnknown"/>. Nothing keeps running after this method returns.
    /// </param>
    Task<CatalogWriteResult<T>> ExecuteAsync<T>(
        Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
        Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verifyAfterUnknownCommit,
        CancellationToken cancellationToken,
        CancellationToken deadline = default) where T : class;
}
