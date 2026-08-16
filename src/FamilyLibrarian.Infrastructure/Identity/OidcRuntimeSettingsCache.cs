using FamilyLibrarian.Application.Accounts;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>
/// A singleton, lock-free holder for the currently-active OIDC configuration.
/// </summary>
/// <remarks>
/// Reference assignment is atomic in .NET, so <see cref="Current"/> is always a
/// fully-formed, consistent snapshot — never a value torn between an in-flight
/// <see cref="Refresh"/> and a concurrent read.
/// </remarks>
public sealed class OidcRuntimeSettingsCache : IOidcRuntimeSettingsCache
{
    private OidcRuntimeSettings current = OidcRuntimeSettings.Disabled;

    public OidcRuntimeSettings Current => current;

    public void Refresh(OidcRuntimeSettings settings) => current = settings;
}
