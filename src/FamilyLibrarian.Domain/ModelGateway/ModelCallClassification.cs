namespace FamilyLibrarian.Domain.ModelGateway;

/// <summary>How a recorded call ended, in the plain terms the insight uses. A valid hold or rejection of a book is still an accepted reply.</summary>
public enum ModelCallCategory
{
    AcceptedReply,
    RejectedReply,
    ProviderError,
    Cancelled,
    Pending,
    Unclassified
}

/// <summary>The same classification the administrator screens use, for code that must not depend on the web contracts (the weekly rollups).</summary>
public static class ModelCallClassification
{
    public static ModelCallCategory Classify(ModelCallOutcome outcome, ModelFailureReason? reason) =>
        reason == ModelFailureReason.Cancelled ? ModelCallCategory.Cancelled : outcome switch
        {
            ModelCallOutcome.Succeeded => ModelCallCategory.AcceptedReply,
            ModelCallOutcome.EmptyContent or ModelCallOutcome.InvalidJson or ModelCallOutcome.SchemaRejected or
                ModelCallOutcome.OutputTooLarge or ModelCallOutcome.OutputLimitReached => ModelCallCategory.RejectedReply,
            ModelCallOutcome.TransportFailure or ModelCallOutcome.Timeout or ModelCallOutcome.ModelUnavailable or
                ModelCallOutcome.UnsupportedCapability => ModelCallCategory.ProviderError,
            ModelCallOutcome.Pending => ModelCallCategory.Pending,
            _ => ModelCallCategory.Unclassified
        };
}

/// <summary>
/// One week of counts for one role, model, placement and opportunity combination, kept after the 90-day ledger prune so the trend
/// survives. Counts only: no user id, subject, title or text. Percentiles of a week split across two prunes are approximate.
/// </summary>
public sealed class ModelUsageRollup
{
    private ModelUsageRollup()
    {
    }

    public ModelUsageRollup(DateTimeOffset weekStartUtc, ModelRole role, string model, bool isCloud, int opportunityFlags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Id = Guid.NewGuid();
        WeekStartUtc = weekStartUtc;
        Role = role;
        Model = model.Length <= ModelGatewaySettings.MaxModelNameLength ? model : model[..ModelGatewaySettings.MaxModelNameLength];
        IsCloud = isCloud;
        OpportunityFlags = opportunityFlags & ModelWorkflowEffects.OpportunityMask;
    }

    public Guid Id { get; private set; }

    /// <summary>Monday 00:00 UTC of the week.</summary>
    public DateTimeOffset WeekStartUtc { get; private set; }

    public ModelRole Role { get; private set; }

    public string Model { get; private set; } = "";

    public bool IsCloud { get; private set; }

    /// <summary>Which opportunity flags the calls in this row were made under (a combination, so different situations stay apart).</summary>
    public int OpportunityFlags { get; private set; }

    public int Calls { get; private set; }

    public int AcceptedReplies { get; private set; }

    public int RejectedReplies { get; private set; }

    public int ProviderErrors { get; private set; }

    public int CancelledCalls { get; private set; }

    public int UnclassifiedCalls { get; private set; }

    public int ActionCalls { get; private set; }

    public int NoChangeCalls { get; private set; }

    public int AbstainedCalls { get; private set; }

    public int AdvisoryCalls { get; private set; }

    public long ReportedTokens { get; private set; }

    public int CallsWithoutUsage { get; private set; }

    public long LatencyMillisecondsSum { get; private set; }

    public long LatencyMillisecondsMax { get; private set; }

    public int LatencyCalls { get; private set; }

    public long LatencyP95Milliseconds { get; private set; }

    public int DownstreamPositive { get; private set; }

    public int DownstreamNegative { get; private set; }

    /// <summary>Adds one call's counts. Pending calls are never rolled up (they are not finished).</summary>
    public void Add(ModelCallCategory category, int? effects, int? reportedTokens, long? latencyMilliseconds, bool? downstreamPositive)
    {
        Calls++;
        switch (category)
        {
            case ModelCallCategory.AcceptedReply: AcceptedReplies++; break;
            case ModelCallCategory.RejectedReply: RejectedReplies++; break;
            case ModelCallCategory.ProviderError: ProviderErrors++; break;
            case ModelCallCategory.Cancelled: CancelledCalls++; break;
            default: UnclassifiedCalls++; break;
        }

        var bits = effects ?? 0;
        if ((bits & ModelWorkflowEffects.ActionMask) != 0) ActionCalls++;
        if ((bits & (int)ModelWorkflowEffect.NoChange) != 0) NoChangeCalls++;
        if ((bits & (int)ModelWorkflowEffect.Abstained) != 0) AbstainedCalls++;
        if ((bits & (int)ModelWorkflowEffect.AdvisorySaved) != 0) AdvisoryCalls++;
        if (reportedTokens is { } tokens) ReportedTokens += tokens; else CallsWithoutUsage++;
        if (latencyMilliseconds is { } latency)
        {
            LatencyCalls++;
            LatencyMillisecondsSum += latency;
            if (latency > LatencyMillisecondsMax) LatencyMillisecondsMax = latency;
        }

        if (downstreamPositive == true) DownstreamPositive++;
        else if (downstreamPositive == false) DownstreamNegative++;
    }

    /// <summary>Keeps the larger p95 when a week is rolled up in two batches: an upper bound, never an understatement.</summary>
    public void MergeLatencyP95(long p95Milliseconds)
    {
        if (p95Milliseconds > LatencyP95Milliseconds) LatencyP95Milliseconds = p95Milliseconds;
    }
}
