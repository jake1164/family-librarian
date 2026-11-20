namespace FamilyLibrarian.Application.Catalog.Identity;

public enum ProofKind
{
    /// <summary>No compatible edition's ISBN belongs to a saved Work, and no excluded one does either.</summary>
    None,

    /// <summary>The compatible editions' ISBNs belong to exactly one saved Work: the Work the record may be attached to.</summary>
    SingleWork,

    /// <summary>The compatible editions' ISBNs belong to two or more saved Works. Which one is meant is not knowable.</summary>
    MultipleWorks,

    /// <summary>The only overlap with a saved Work is an excluded edition's ISBN: not proof, and not a collision either.</summary>
    ExcludedEditionOverlapOnly
}

/// <param name="WorkIds">The Works the compatible editions' ISBNs belong to (one for <see cref="ProofKind.SingleWork"/>).</param>
public sealed record ProofAssessment(ProofKind Kind, IReadOnlyList<Guid> WorkIds)
{
    public Guid? TargetWorkId => Kind == ProofKind.SingleWork ? WorkIds[0] : null;
}

/// <summary>
/// Turns the candidate-intrinsic edition projection and the saved owners of its ISBNs into the proof a Work attachment needs. Only
/// compatible editions are proof; an excluded edition's ISBN never counts, neither as agreement nor toward a multi-Work collision.
/// </summary>
public static class CatalogProof
{
    /// <param name="candidateIntrinsic">The projection with no target, so nothing here depends on a Work yet.</param>
    /// <param name="ownerByIsbn">For each of the record's ISBN-13s that a non-retired saved Work holds, that Work's id.</param>
    public static ProofAssessment Assess(EditionProjection candidateIntrinsic, IReadOnlyDictionary<string, Guid> ownerByIsbn)
    {
        ArgumentNullException.ThrowIfNull(candidateIntrinsic);
        ArgumentNullException.ThrowIfNull(ownerByIsbn);

        var owners = candidateIntrinsic.ProofIsbns
            .Where(ownerByIsbn.ContainsKey)
            .Select(isbn => ownerByIsbn[isbn])
            .Distinct()
            .Order()
            .ToArray();
        if (owners.Length == 1)
        {
            return new ProofAssessment(ProofKind.SingleWork, owners);
        }

        if (owners.Length > 1)
        {
            return new ProofAssessment(ProofKind.MultipleWorks, owners);
        }

        return candidateIntrinsic.ExcludedOnlyIsbns.Any(ownerByIsbn.ContainsKey)
            ? new ProofAssessment(ProofKind.ExcludedEditionOverlapOnly, [])
            : new ProofAssessment(ProofKind.None, []);
    }
}
