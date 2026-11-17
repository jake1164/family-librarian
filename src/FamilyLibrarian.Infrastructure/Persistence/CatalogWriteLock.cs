using FamilyLibrarian.Application.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FamilyLibrarian.Infrastructure.Persistence;

/// <summary>
/// The one cross-process lock every cooperating catalog writer takes, as a transaction-scoped PostgreSQL advisory lock.
/// </summary>
/// <remarks>
/// A transaction-scoped lock is released by commit, rollback, or the connection ending, so a crashed or cancelled writer cannot
/// strand it (PostgreSQL, "Explicit Locking", advisory locks). The key space is separate from the request lock
/// <c>(831704, workHash)</c> in <see cref="RequestRepository"/>; the two do not interact. Waiting is bounded by
/// <c>lock_timeout</c>, so a stuck holder surfaces as SQLSTATE 55P03 instead of an unbounded wait.
/// </remarks>
internal static class CatalogWriteLock
{
    /// <summary>First advisory key: the catalog namespace.</summary>
    internal const int Namespace = 831705;

    /// <summary>Second advisory key: one catalog-wide lock, because Author/Series rows have no unique index to arbitrate on.</summary>
    internal const int Key = 0;

    /// <summary>SQLSTATE raised when <c>lock_timeout</c> expires.</summary>
    internal const string LockNotAvailable = "55P03";

    /// <summary>
    /// Begins a transaction on <paramref name="database"/>, bounds lock waits for it, and takes the catalog lock. The returned
    /// transaction owns the lock; on any failure here the transaction has already been disposed (rolled back).
    /// </summary>
    internal static async Task<IDbContextTransaction> BeginAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // SET LOCAL lasts only for this transaction and cannot be parameterized; the value is a constant.
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
            await database.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Namespace}, {Key})", cancellationToken);
            return transaction;
        }
        catch
        {
            await DisposeQuietlyAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// The id of the current transaction if it has written anything (so the database has assigned it one), otherwise null. Read just
    /// before COMMIT so that, if the outcome of the COMMIT is ever unknown, the database itself can later say what became of it
    /// (<c>pg_xact_status</c>) without any clock or row comparison. A read-only attempt is never given an id by asking.
    /// </summary>
    internal static async Task<string?> AssignedTransactionIdAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        // pg_current_xact_id_if_assigned() returns the 64-bit xid8, which does not wrap, so the id cannot be confused with a later one.
        var rows = await database.Database
            .SqlQuery<string>($"SELECT COALESCE(pg_current_xact_id_if_assigned()::text, '') AS \"Value\"")
            .ToListAsync(cancellationToken);
        return rows.Count == 1 && rows[0].Length > 0 ? rows[0] : null;
    }

    /// <summary>
    /// What PostgreSQL records about a recent transaction: committed, rolled back, still running, or (if the database no longer
    /// retains it) nothing. Intended to be asked while holding the catalog lock, after which the original transaction, which held
    /// the same lock, has necessarily ended.
    /// </summary>
    internal static async Task<CatalogTransactionStatus> TransactionStatusAsync(
        AppDbContext database, string? transactionId, CancellationToken cancellationToken)
    {
        if (transactionId is null)
        {
            return CatalogTransactionStatus.NoWrites;
        }

        var rows = await database.Database
            .SqlQuery<string>($"SELECT COALESCE(pg_xact_status(CAST(CAST({transactionId} AS text) AS xid8)), 'unknown') AS \"Value\"")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault() switch
        {
            "committed" => CatalogTransactionStatus.Committed,
            "aborted" => CatalogTransactionStatus.RolledBack,
            _ => CatalogTransactionStatus.Unknown
        };
    }

    /// <summary>
    /// Disposing a transaction on a connection that has already failed can itself throw; the rollback it was attempting is
    /// implied by the server having dropped the session, so that secondary failure must not hide the original one.
    /// </summary>
    internal static async Task DisposeQuietlyAsync(IDbContextTransaction transaction)
    {
#pragma warning disable CA1031 // Deliberate: see the summary.
        try
        {
            await transaction.DisposeAsync();
        }
        catch (Exception)
        {
            // The first failure is the one that matters.
        }
#pragma warning restore CA1031
    }
}
