using System.Net.Sockets;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Following;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FamilyLibrarian.Infrastructure.Catalog;

/// <summary>
/// PostgreSQL catalog write scope: one fresh dependency-injection scope, <see cref="AppDbContext"/>, connection and transaction per
/// attempt, serialized by the catalog advisory lock (see <see cref="CatalogWriteLock"/>).
/// </summary>
/// <remarks>
/// <para>
/// No execution strategy is configured for <see cref="AppDbContext"/>, so a manually controlled transaction is valid; if retrying
/// execution strategies are ever enabled this class must be re-examined (EF Core, "Using Transactions").
/// </para>
/// <para>
/// A failed attempt is rolled back and its scope, context and tracked entities are discarded before anything is retried, so a
/// retry re-reads authoritative state and never reuses an entity from the abandoned transaction. At most one retry is made, and
/// only for a unique-index loss, a serialization failure, a deadlock, an optimistic-concurrency conflict, or a commit whose
/// outcome was unknown and verified as not having happened. EF Core savepoints do not matter here: the attempt is always
/// discarded whole.
/// </para>
/// </remarks>
public sealed partial class PostgresCatalogWriteScope(
    IServiceScopeFactory scopes,
    ILogger<PostgresCatalogWriteScope> logger) : ICatalogWriteScope
{
    private const int MaximumAttempts = 2;

    public async Task<CatalogWriteResult<T>> ExecuteAsync<T>(
        Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
        Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verifyAfterUnknownCommit,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(verifyAfterUnknownCommit);

        var cause = CatalogWriteRetryCause.None;
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await RunAttemptAsync(mutation, cause, cancellationToken);
            switch (outcome.Kind)
            {
                case AttemptKind.Committed:
                    return new CatalogWriteResult<T>(CatalogWriteStatus.Committed, outcome.Value);
                case AttemptKind.Busy:
                    return new CatalogWriteResult<T>(CatalogWriteStatus.Busy, null);
                case AttemptKind.Unavailable:
                    return new CatalogWriteResult<T>(CatalogWriteStatus.Unavailable, null);
                case AttemptKind.Retryable:
                    LogRetryable(attempt, outcome.Cause);
                    if (attempt >= MaximumAttempts)
                    {
                        return new CatalogWriteResult<T>(CatalogWriteStatus.Conflict, null);
                    }

                    cause = outcome.Cause;
                    continue;
                default:
                    // The commit's outcome is unknown. Find out, holding the same lock so an in-flight first transaction settles.
                    var verified = await VerifyAsync(outcome.Value!, verifyAfterUnknownCommit, cancellationToken);
                    switch (verified.Kind)
                    {
                        case VerifyKind.Present:
                            return new CatalogWriteResult<T>(CatalogWriteStatus.Recovered, verified.Value);
                        case VerifyKind.Unprovable:
                            return new CatalogWriteResult<T>(CatalogWriteStatus.OutcomeUnknown, null);
                    }

                    // Proven absent: it did not happen, so the one retry is safe.
                    if (attempt >= MaximumAttempts)
                    {
                        return new CatalogWriteResult<T>(CatalogWriteStatus.Unavailable, null);
                    }

                    cause = CatalogWriteRetryCause.UncertainCommit;
                    continue;
            }
        }
    }

    private async Task<Attempt<T>> RunAttemptAsync<T>(
        Func<ICatalogWriteSession, CancellationToken, Task<T>> mutation,
        CatalogWriteRetryCause cause,
        CancellationToken cancellationToken) where T : class
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IDbContextTransaction transaction;
        try
        {
            transaction = await CatalogWriteLock.BeginAsync(database, cancellationToken);
        }
        catch (Exception exception) when (ClassifyBeforeCommit<T>(exception, cancellationToken) is { } classified)
        {
            LogFault("begin", classified.Kind, SqlStateOf(exception));
            return classified;
        }

        try
        {
            T value;
            try
            {
                value = await mutation(new Session(scope.ServiceProvider, cause), cancellationToken);
            }
            catch (Exception exception) when (ClassifyBeforeCommit<T>(exception, cancellationToken) is { } classified)
            {
                LogFault("mutate", classified.Kind, SqlStateOf(exception));
                return classified;
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception) when (ClassifyCommit(exception, value, cancellationToken) is { } classified)
            {
                LogFault("commit", classified.Kind, SqlStateOf(exception));
                return classified;
            }

            return new Attempt<T>(AttemptKind.Committed, CatalogWriteRetryCause.None, value);
        }
        finally
        {
            await CatalogWriteLock.DisposeQuietlyAsync(transaction);
        }
    }

    private async Task<Verification<T>> VerifyAsync<T>(
        T attempted,
        Func<ICatalogWriteSession, T, CancellationToken, Task<T?>> verify,
        CancellationToken cancellationToken) where T : class
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            var transaction = await CatalogWriteLock.BeginAsync(database, cancellationToken);
            try
            {
                var found = await verify(new Session(scope.ServiceProvider, CatalogWriteRetryCause.UncertainCommit), attempted, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return found is null ? new Verification<T>(VerifyKind.Absent, null) : new Verification<T>(VerifyKind.Present, found);
            }
            finally
            {
                await CatalogWriteLock.DisposeQuietlyAsync(transaction);
            }
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken)
            && (IsLockTimeout(exception) || IsTransportFailure(exception) || IsServerSessionFailure(exception)))
        {
            // Neither "it committed" nor "it did not" could be established.
            LogFault("verify", AttemptKind.UnknownCommit, SqlStateOf(exception));
            return new Verification<T>(VerifyKind.Unprovable, null);
        }
    }

    private static Attempt<T>? ClassifyBeforeCommit<T>(Exception exception, CancellationToken cancellationToken) where T : class
    {
        if (IsCallerCancellation(exception, cancellationToken))
        {
            return null;
        }

        if (IsLockTimeout(exception))
        {
            return new Attempt<T>(AttemptKind.Busy, CatalogWriteRetryCause.None, null);
        }

        if (RetryCauseOf(exception) is { } cause)
        {
            return new Attempt<T>(AttemptKind.Retryable, cause, null);
        }

        // The transaction never reached COMMIT, so a transport or session failure here wrote nothing.
        return IsTransportFailure(exception) || IsServerSessionFailure(exception)
            ? new Attempt<T>(AttemptKind.Unavailable, CatalogWriteRetryCause.None, null)
            : null;
    }

    private static Attempt<T>? ClassifyCommit<T>(Exception exception, T value, CancellationToken cancellationToken) where T : class
    {
        if (IsCallerCancellation(exception, cancellationToken))
        {
            return null;
        }

        // The server answered the COMMIT with an error: the transaction is definitively rolled back.
        if (FindPostgres(exception) is { } postgres && !IsServerSessionState(postgres.SqlState))
        {
            return RetryCauseOf(exception) is { } cause
                ? new Attempt<T>(AttemptKind.Retryable, cause, null)
                : null;
        }

        // The connection or session failed around the COMMIT: it may or may not have been applied.
        return IsTransportFailure(exception) || IsServerSessionFailure(exception) || exception is OperationCanceledException or ObjectDisposedException
            ? new Attempt<T>(AttemptKind.UnknownCommit, CatalogWriteRetryCause.None, value)
            : null;
    }

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && (exception is OperationCanceledException || exception.InnerException is OperationCanceledException);

    private static PostgresException? FindPostgres(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static string? SqlStateOf(Exception exception) => FindPostgres(exception)?.SqlState;

    private static bool IsLockTimeout(Exception exception) =>
        FindPostgres(exception)?.SqlState == CatalogWriteLock.LockNotAvailable;

    private static CatalogWriteRetryCause? RetryCauseOf(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return CatalogWriteRetryCause.ConcurrentUpdate;
        }

        return FindPostgres(exception)?.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => CatalogWriteRetryCause.UniqueViolation,
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected => CatalogWriteRetryCause.ConcurrentUpdate,
            _ => null
        };
    }

    /// <summary>SQLSTATE classes meaning the server session itself ended or failed (08 connection exception, 57P0x shutdown).</summary>
    private static bool IsServerSessionState(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal) ||
        sqlState is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown or PostgresErrorCodes.CannotConnectNow;

    private static bool IsServerSessionFailure(Exception exception) =>
        FindPostgres(exception) is { } postgres && IsServerSessionState(postgres.SqlState);

    private static bool IsTransportFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if ((current is NpgsqlException && current is not PostgresException) ||
                current is System.IO.IOException or SocketException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Catalog write {Boundary} did not complete: {Kind} (SQLSTATE {SqlState}).")]
    private partial void LogFault(string boundary, AttemptKind kind, string? sqlState);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Catalog write attempt {Attempt} lost a conflict ({Cause}); re-reading catalog state.")]
    private partial void LogRetryable(int attempt, CatalogWriteRetryCause cause);

    private enum AttemptKind
    {
        Committed,
        Busy,
        Unavailable,
        Retryable,
        UnknownCommit
    }

    private enum VerifyKind
    {
        Present,
        Absent,
        Unprovable
    }

    private sealed record Attempt<T>(AttemptKind Kind, CatalogWriteRetryCause Cause, T? Value) where T : class;

    private sealed record Verification<T>(VerifyKind Kind, T? Value) where T : class;

    /// <summary>The repositories one attempt may use, all resolved from that attempt's own scope.</summary>
    private sealed class Session(IServiceProvider services, CatalogWriteRetryCause cause) : ICatalogWriteSession
    {
        public ICatalogMutationRepository Catalog { get; } = services.GetRequiredService<ICatalogMutationRepository>();

        public IFollowRepository Follows { get; } = services.GetRequiredService<IFollowRepository>();

        public CatalogWriteRetryCause RetryCause => cause;
    }
}
