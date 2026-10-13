using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Profile facts for an M4B (AUTOFIND 3c): its total listening time and the names of its first ten Nero chapters. A set of
/// several files records its total once across all tracks instead (<see cref="AudioBundleProfile"/>).
/// </summary>
public sealed class M4bProfileInspector : IAssetProfileInspector
{
    internal const int MaxChapterTitles = 10;

    public string Id => "m4b-profile";

    public bool Supports(MediaAsset asset) => string.Equals(asset.Format, ".m4b", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        var facts = new List<IdentityFact>();
        if (asset.BundleId is null &&
            AudioDurations.TryReadSeconds(await M4bAssetIdentityVerifier.InspectTechnicalAsync(content, cancellationToken), out var seconds))
        {
            facts.Add(AudioDurations.TotalDuration(seconds, tracks: 1));
        }

        var titles = await M4bAssetIdentityVerifier.ReadChapterTitlesAsync(content, MaxChapterTitles, cancellationToken);
        if (titles.Count > 0)
        {
            facts.Add(new IdentityFact("ev-profile-chapters", EvidenceDimension.Profile,
                EvidenceReasonCodes.ArtifactProfile.AudioChapterTitles, EvidencePolarity.Unknown, string.Join(" / ", titles)));
        }

        return facts;
    }
}

/// <summary>Profile facts for an MP3 (AUTOFIND 3c): its own total listening time when it is a single file.</summary>
public sealed class Mp3ProfileInspector : IAssetProfileInspector
{
    private readonly Mp3TechnicalInspector _technical = new();

    public string Id => "mp3-profile";

    public bool Supports(MediaAsset asset) => string.Equals(asset.Format, ".mp3", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken) =>
        asset.BundleId is null &&
        AudioDurations.TryReadSeconds(await _technical.InspectAsync(asset, content, cancellationToken), out var seconds)
            ? [AudioDurations.TotalDuration(seconds, tracks: 1)]
            : [];
}
