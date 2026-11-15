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

    void AddWork(Work work);

    void AddAuthor(Author author);

    void AddSeries(Series series);

    void AddExternalReference(ExternalReference externalReference);

    Task SaveChangesAsync(CancellationToken cancellationToken);
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

/// <summary>One catalog write attempt: its fresh repositories and why it is running.</summary>
public interface ICatalogWriteSession
{
    ICatalogMutationRepository Catalog { get; }

    /// <summary>Reads follower rows through the same unit of work as <see cref="Catalog"/>.</summary>
    IFollowRepository Follows { get; }

    CatalogWriteRetryCause RetryCause { get; }
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
    Unavailable
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
    /// result and returns the committed equivalent when the operation's effects are demonstrably present, or null when they are not.
    /// </param>
    Task<CatalogWriteResult<T>> ExecuteAsync<T>(
        Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
        Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verifyAfterUnknownCommit,
        CancellationToken cancellationToken) where T : class;
}
