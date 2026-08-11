using System.Security.Claims;
using FamilyLibrarian.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace FamilyLibrarian.Infrastructure.Identity;

public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }

    public string? DisplayName
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return principal.FindFirstValue(ClaimTypes.Email)
                ?? principal.FindFirstValue(ClaimTypes.Name)
                ?? principal.Identity.Name;
        }
    }
}
