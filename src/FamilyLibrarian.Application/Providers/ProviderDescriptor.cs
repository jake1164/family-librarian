using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Describes one installed provider: what it is, what it can do, whether it
/// needs a credential, and what the deployment already decided about it.
/// </summary>
/// <param name="Capabilities">
/// What this provider can do. A provider may advertise more than one
/// capability; core code consumes capabilities rather than assuming every
/// installed integration is an acquisition source.
/// </param>
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
/// The deployment's default-enabled value, used only until an administrator
/// saves a setting for this provider. Once a stored row exists it is
/// authoritative, so an admin's choice is not silently reverted by config.
/// </param>
/// <param name="SetupInstructions">
/// Short, plain-text information about using or configuring the provider. It is
/// shown in the provider's administrator card and may be present even when the
/// provider needs no credential.
/// </param>
/// <param name="SetupLinks">
/// Named links (for example, a provider's website, documentation, or the exact
/// console page to enable an API) shown alongside <see cref="SetupInstructions"/>.
/// </param>
public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    IReadOnlySet<ProviderCapability> Capabilities,
    bool RequiresCredential,
    bool HasExternallyManagedCredential,
    bool DefaultEnabled,
    string? SetupInstructions = null,
    IReadOnlyList<ProviderSetupLink>? SetupLinks = null);

/// <param name="Label">What the link is for, e.g. "Enable the Books API".</param>
/// <param name="Url">The destination. Always an external, informational URL —
/// never a target the host itself calls.</param>
public sealed record ProviderSetupLink(string Label, string Url);
