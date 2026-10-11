using FamilyLibrarian.Application.Abstractions;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>
/// <see cref="ICurrentUser"/> for every scope: the authenticated non-HTTP operation principal when one was established
/// (alpha.6 M2-1), otherwise exactly the HTTP behavior the application always had. Once a principal is established the
/// HTTP context is never consulted, so a request cannot borrow another identity; a scope with neither reports nobody.
/// </summary>
public sealed class OperationAwareCurrentUser(IOperationPrincipalAccessor principal, HttpContextCurrentUser http) : ICurrentUser
{
    public Guid? UserId => principal.Current is { } current ? current.UserId : http.UserId;

    public string? DisplayName => principal.Current is { } current ? current.DisplayName : http.DisplayName;
}

/// <summary><see cref="ICurrentAdministrator"/> counterpart of <see cref="OperationAwareCurrentUser"/>.</summary>
public sealed class OperationAwareCurrentAdministrator(IOperationPrincipalAccessor principal, HttpContextCurrentAdministrator http)
    : ICurrentAdministrator
{
    public bool IsAdministrator => principal.Current is { } current ? current.IsAdministrator : http.IsAdministrator;
}
