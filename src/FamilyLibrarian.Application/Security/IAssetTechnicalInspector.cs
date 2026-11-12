using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

/// <summary>
/// Measures technical properties of a staged audiobook file from its headers (alpha.6 M1-4c). Evidence only: its output is
/// recorded in the <c>AudioTechnical</c> dimension after the security decision is saved, and nothing it reports is an input to
/// identity verification, the hold policy, approval or publishing. Implementations read bounded header data, never decode or
/// transcribe audio, and never guess a value they could not measure.
/// </summary>
public interface IAssetTechnicalInspector
{
    string Id { get; }

    bool Supports(MediaAsset asset);

    /// <returns>Findings in the AudioTechnical dimension; empty when nothing could be read.</returns>
    Task<IReadOnlyList<IdentityFact>> InspectAsync(MediaAsset asset, Stream content, CancellationToken cancellationToken);
}
