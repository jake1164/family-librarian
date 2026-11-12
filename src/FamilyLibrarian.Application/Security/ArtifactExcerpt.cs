using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

/// <summary>Transient book text. Never attach this object to evidence, findings, diagnostics or API responses.</summary>
public sealed record ArtifactExcerpt(string Id, string Text)
{
    public override string ToString() => Id;
}

public interface IArtifactExcerptExtractor
{
    Task<IReadOnlyList<ArtifactExcerpt>> ExtractAsync(MediaAsset asset, CancellationToken cancellationToken);
}
