using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>Measures an M4B's duration, codec, bitrate and chapter list from its container headers (alpha.6 M1-4c). Evidence only.</summary>
public sealed class M4bTechnicalInspector : IAssetTechnicalInspector
{
    public string Id => "m4b-headers";

    public bool Supports(MediaAsset asset) => string.Equals(asset.Format, ".m4b", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
        M4bAssetIdentityVerifier.InspectTechnicalAsync(content, cancellationToken);
}
