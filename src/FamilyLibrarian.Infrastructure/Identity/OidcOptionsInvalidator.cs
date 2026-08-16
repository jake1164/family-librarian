using FamilyLibrarian.Application.Accounts;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Identity;

public sealed class OidcOptionsInvalidator(IOptionsMonitorCache<OpenIdConnectOptions> optionsCache)
    : IOidcOptionsInvalidator
{
    public void Invalidate() => optionsCache.TryRemove(OidcOptionsConfigurator.SchemeName);
}
