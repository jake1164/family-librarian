using FamilyLibrarian.Contracts.ModelGateway;

namespace FamilyLibrarian.Web.Client.ModelGateway;

public sealed record AiWorkflowMetrics(int Usable, int Observed, int ActionCalls, int AdvisoryCalls, int NoChangeCalls,
    IReadOnlyList<(string Label, int Calls)> Effects)
{
    public int NotObserved => Usable - Observed;
    private static readonly string[] Actions = ["IntentUsed", "CoverSearchCompleted", "ExtraSearchCompleted", "ExtraSearchReturnedResults",
        "CandidateVetoUsed", "CandidateSelectionUsed", "FileHoldApplied", "FileReleaseApplied"];

    public static AiWorkflowMetrics From(IEnumerable<ModelUsageRowResponse> source)
    {
        var rows = source.Where(row => row.Outcome == "Succeeded").ToArray();
        bool HasAction(ModelUsageRowResponse row) => row.WorkflowEffects?.Any(Actions.Contains) == true;
        var effects = rows.Where(row => row.WorkflowEffects is not null).SelectMany(row => row.WorkflowEffects!.Distinct(StringComparer.Ordinal)
                .Select(effect => (Effect: effect, row.Calls)))
            .Where(item => Actions.Contains(item.Effect)).GroupBy(item => item.Effect).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Label(group.Key), group.Sum(item => item.Calls))).ToArray();
        return new(rows.Sum(row => row.Calls), rows.Where(row => row.WorkflowEffects is not null).Sum(row => row.Calls),
            rows.Where(HasAction).Sum(row => row.Calls),
            rows.Where(row => row.WorkflowEffects?.Contains("AdvisorySaved") == true).Sum(row => row.Calls),
            rows.Where(row => !HasAction(row) && row.WorkflowEffects?.Any(effect => effect is "NoChange" or "Abstained") == true).Sum(row => row.Calls), effects);
    }

    private static string Label(string effect) => effect switch
    {
        "IntentUsed" => "Intent used in response planning", "CoverSearchCompleted" => "Cover-based catalog search completed",
        "ExtraSearchCompleted" => "Extra search completed", "ExtraSearchReturnedResults" => "Extra search returned results",
        "CandidateVetoUsed" => "Candidate veto used", "CandidateSelectionUsed" => "Candidate selection used",
        "FileHoldApplied" => "File hold applied", "FileReleaseApplied" => "File hold released", _ => "Other observed use"
    };
}
