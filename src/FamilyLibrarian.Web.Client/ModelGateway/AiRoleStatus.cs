using FamilyLibrarian.Contracts.ModelGateway;

namespace FamilyLibrarian.Web.Client.ModelGateway;

public enum AiRoleStatusKind
{
    NotUsed,
    TooLittleData,
    Failing,
    DoingWork,
    SafeButIdle
}

/// <summary>
/// A plain per-feature status computed by fixed rules from the counts already on screen. It is explicitly not a quality score: it says
/// whether the feature is doing anything and whether it is failing, never whether it is right. Order of rules: no calls; failing (a
/// stated share of evaluated calls rejected or errored, with enough calls to say so); too little data; doing work; safe but idle.
/// </summary>
public sealed record AiRoleStatus(AiRoleStatusKind Kind, string Label, string Reason)
{
    /// <summary>Fewer accepted calls than this and the screen says "too little data" instead of guessing.</summary>
    public const int MinAcceptedCalls = 20;

    /// <summary>More than this share of evaluated calls rejected or errored is "failing" (strictly more than half).</summary>
    public const int FailingPercentAbove = 50;

    public static AiRoleStatus From(IEnumerable<ModelUsageRowResponse> source)
    {
        var rows = source.ToArray();
        var quality = AiQualityMetrics.From(rows);
        if (quality.Calls == 0) return new(AiRoleStatusKind.NotUsed, "No calls", "No calls in this period.");

        var evaluated = quality.Replies + quality.ProviderErrors;
        var failed = quality.UnusableReplies + quality.ProviderErrors;
        if (evaluated >= MinAcceptedCalls && failed * 100 > evaluated * FailingPercentAbove)
            return new(AiRoleStatusKind.Failing, "Failing", $"{failed:N0} of {evaluated:N0} evaluated calls were rejected or failed.");

        if (quality.Usable < MinAcceptedCalls)
            return new(AiRoleStatusKind.TooLittleData, "Too little data",
                $"{quality.Usable:N0} accepted {(quality.Usable == 1 ? "call" : "calls")}; at least {MinAcceptedCalls} are needed to say more.");

        var workflow = AiWorkflowMetrics.From(rows);
        if (workflow.ActionCalls > 0)
            return new(AiRoleStatusKind.DoingWork, "Doing work", $"A workflow action was applied in {workflow.ActionCalls:N0} of {quality.Usable:N0} accepted calls.");
        if (workflow.AdvisoryCalls > 0)
            return new(AiRoleStatusKind.DoingWork, "Doing work", $"Advice was saved on {workflow.AdvisoryCalls:N0} of {quality.Usable:N0} accepted calls.");
        return new(AiRoleStatusKind.SafeButIdle, "Safe but idle",
            $"Accepted calls agreed, abstained or changed nothing ({workflow.NoChangeCalls:N0} of {quality.Usable:N0} observed as such); no action was applied.");
    }
}
