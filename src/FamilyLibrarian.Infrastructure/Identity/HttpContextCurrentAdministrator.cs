using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain;
using Microsoft.AspNetCore.Http;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>Administrator status read from the same role claim the "Admin" authorization policy uses.</summary>
public sealed class HttpContextCurrentAdministrator(IHttpContextAccessor accessor) : ICurrentAdministrator
{
    public bool IsAdministrator =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user && user.IsInRole(RoleNames.Admin);
}
