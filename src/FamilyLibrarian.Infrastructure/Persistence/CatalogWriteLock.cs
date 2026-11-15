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
