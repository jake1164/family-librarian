namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// One alpha.6 M1-0 characterization case: what the CURRENT code does for a
/// fixed input, pinned so later M1 slices change outcomes deliberately.
/// </summary>
/// <param name="Id">Stable id (C = candidate, E = EPUB, A = audiobook, R = resolver guard).</param>
/// <param name="Observed">The outcome production code produces today. Asserted exactly.</param>
/// <param name="Desired">
/// Null when <paramref name="Observed"/> is already the intended behavior. When
/// non-null the case is a KnownGap: today's outcome is pinned, this is what a
/// later slice should produce, and fixing it must flip the case and update
/// <see cref="KnownGapRegistryTests"/> in the same PR.
/// </param>
public sealed record CharacterizationCase(string Id, string Description, string Observed, string? Desired = null)
{
    public bool IsKnownGap => Desired is not null;
}
