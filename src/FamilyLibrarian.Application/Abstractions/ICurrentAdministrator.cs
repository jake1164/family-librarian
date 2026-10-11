namespace FamilyLibrarian.Application.Abstractions;

/// <summary>
/// Whether the current caller is an authenticated administrator. A service that grants an
/// administrator-only authority (for example vouching for an uploaded file's identity) checks this
/// itself rather than relying only on the route that happens to call it.
/// </summary>
public interface ICurrentAdministrator
{
    bool IsAdministrator { get; }
}
