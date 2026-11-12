namespace FamilyLibrarian.Application.Abstractions;

/// <summary>
/// The authenticated Family Librarian account behind one non-HTTP operation (alpha.6 M2-1), such as a
/// message received over Matrix. Every field comes from the stored account at the moment the operation was
/// authenticated: the identifier and display name are the account's, the administrator flag is its current
/// role. Nothing a remote party supplied (a claimed user id, a Matrix display name) is copied in.
/// </summary>
/// <param name="Channel">Where the operation arrived from, for diagnostics only; never an authorization input.</param>
public sealed record OperationPrincipal(Guid UserId, string DisplayName, bool IsAdministrator, string Channel);

/// <summary>Read side: the principal established for the current dependency-injection scope, if any.</summary>
public interface IOperationPrincipalAccessor
{
    OperationPrincipal? Current { get; }
}

/// <summary>
/// Holds the principal for exactly one dependency-injection scope (one inbound message, one conversation turn).
/// Registered scoped, never static or singleton, so one operation's identity cannot be observed by another.
/// </summary>
/// <remarks>
/// <see cref="Establish"/> is <c>internal</c>: only code in this assembly that has itself authenticated the caller
/// (such as <see cref="Communications.MatrixPrincipalAuthenticator"/>) can set it, and it can be set once. A scope
/// that was never authenticated has no principal, so <see cref="ICurrentUser"/> reports nobody and services fail closed.
/// </remarks>
public sealed class OperationPrincipalHolder : IOperationPrincipalAccessor
{
    private readonly Lock gate = new();
    private OperationPrincipal? current;

    public OperationPrincipal? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    internal void Establish(OperationPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        lock (gate)
        {
            if (current is not null)
            {
                throw new InvalidOperationException("An operation's principal is established once and cannot be replaced.");
            }

            current = principal;
        }
    }
}
