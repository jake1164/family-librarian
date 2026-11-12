using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Describes a staged audiobook file for the artifact profile (AUTOFIND 3c): facts in the
/// <see cref="Domain.Evidence.EvidenceDimension.Profile"/> dimension such as the total duration and the chapter names. Like
/// <see cref="IAssetTechnicalInspector"/> it reads bounded header data, never decodes audio, and is evidence only; it is a
/// separate seam so the technical inspectors keep reporting nothing but measurements.
/// </summary>
public interface IAssetProfileInspector
{
    string Id { get; }

    bool Supports(MediaAsset asset);

    /// <returns>Findings in the Profile dimension; empty when nothing could be read.</returns>
    Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken);
}
