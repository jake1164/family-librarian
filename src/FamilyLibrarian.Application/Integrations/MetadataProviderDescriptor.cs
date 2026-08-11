namespace FamilyLibrarian.Application.Integrations;

/// <summary>
/// Describes one installed metadata provider: what it is, whether it needs a
/// credential, and what the deployment already decided about it.
/// </summary>
/// <param name="RequiresCredential">
/// True when the provider cannot function without an API key or token.
/// </param>
/// <param name="HasExternallyManagedCredential">
/// True when a credential arrived through deployment configuration
/// (environment variable or secret manager). Those are a supported read-only
/// override: the admin UI shows them as externally managed and refuses to
/// replace or clear them, because the next deployment would overwrite the
/// change anyway.
/// </param>
/// <param name="DefaultEnabled">
/// The deployment's <c>MetadataProviders:*:Enabled</c> value, used only until an
/// administrator saves a setting for this provider. Once a stored row exists it
/// is authoritative, so an admin's choice is not silently reverted by config.
/// </param>
public sealed record MetadataProviderDescriptor(
    string Id,
    string DisplayName,
    bool RequiresCredential,
    bool HasExternallyManagedCredential,
    bool DefaultEnabled);
