namespace FamilyLibrarian.Domain.Policy;

/// <summary>
/// The fixed set of acquisition-policy profile ids. Shared between the domain
/// entity (for its safe default), the pure ranking logic in Application, and
/// the descriptor registry in Infrastructure — one place, like <c>AuditActions</c>.
/// </summary>
public static class PolicyProfileIds
{
    public const string ManualChoice = "manual-choice";
    public const string LibraryFirst = "library-first";
    public const string FreeFirst = "free-first";
    public const string LowestCost = "lowest-cost";
}
