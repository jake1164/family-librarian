using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.SemanticAssessment;

public sealed class SemanticFindingStore(AppDbContext database) : ISemanticFindingStore
{
    public Task<SemanticFinding?> FindLatestAsync(SemanticSubjectKind kind, string evidenceDigest, CancellationToken cancellationToken) =>
        database.SemanticFindings.AsNoTracking()
            .Where(finding => finding.Kind == kind && finding.EvidenceDigest == evidenceDigest)
            .OrderByDescending(finding => finding.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, SemanticFinding>> FindLatestAsync(
        SemanticSubjectKind kind, IReadOnlyCollection<string> evidenceDigests, CancellationToken cancellationToken)
    {
        if (evidenceDigests.Count == 0)
        {
            return new Dictionary<string, SemanticFinding>();
        }

        var rows = await database.SemanticFindings.AsNoTracking()
            .Where(finding => finding.Kind == kind && evidenceDigests.Contains(finding.EvidenceDigest))
            .ToListAsync(cancellationToken);
        return rows.GroupBy(finding => finding.EvidenceDigest, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(finding => finding.CreatedAtUtc).First(), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, SemanticFinding>> FindLatestForSubjectsAsync(
        SemanticSubjectKind kind, IReadOnlyCollection<string> subjectReferences, CancellationToken cancellationToken)
    {
        if (subjectReferences.Count == 0)
        {
            return new Dictionary<string, SemanticFinding>();
        }

        var rows = await database.SemanticFindings.AsNoTracking()
            .Where(finding => finding.Kind == kind && subjectReferences.Contains(finding.SubjectReference))
            .ToListAsync(cancellationToken);
        return rows.GroupBy(finding => finding.SubjectReference, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(finding => finding.CreatedAtUtc).First(), StringComparer.Ordinal);
    }

    public async Task AddAsync(SemanticFinding finding, CancellationToken cancellationToken)
    {
        database.SemanticFindings.Add(finding);
        await database.SaveChangesAsync(cancellationToken);
    }
}
