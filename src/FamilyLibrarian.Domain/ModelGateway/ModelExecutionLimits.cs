namespace FamilyLibrarian.Domain.ModelGateway;

/// <summary>
/// Which budget a model call is charged to. Interactive chat and background validation never share one, so a burst of
/// chat cannot starve validation and a long validation backlog cannot lock people out of chat.
/// </summary>
public enum ModelBudgetClass
{
    InteractiveChat = 1,
    BackgroundValidation = 2
}

/// <summary>How one model call ended. <see cref="Pending"/> is only ever a row that has not been completed (or whose process died).</summary>
public enum ModelCallOutcome
{
    Pending = 0,
    Succeeded = 1,

    /// <summary>The provider could not be reached, rejected the credentials, rate-limited us or returned a server error.</summary>
    TransportFailure = 2,

    /// <summary>The model did not answer within the allowed time.</summary>
    Timeout = 3,

    /// <summary>The call worked but the model returned no text.</summary>
    EmptyContent = 4,

    /// <summary>The reply was not parseable JSON.</summary>
    InvalidJson = 5,

    /// <summary>The reply was JSON but broke the strict output schema or referenced evidence that was not supplied.</summary>
    SchemaRejected = 6,

    /// <summary>The model or gateway does not support what was asked (JSON mode, tools, images).</summary>
    UnsupportedCapability = 7,

    /// <summary>The configured model alias does not exist or has no healthy deployment.</summary>
    ModelUnavailable = 8,

    /// <summary>The reply was larger than the allowed output size.</summary>
    OutputTooLarge = 9,

    /// <summary>
    /// The provider said it stopped because the output cap was reached (finish reason "length"), so any text is cut off and is
    /// never parsed. Often a thinking model spending the cap on reasoning. Distinct from <see cref="EmptyContent"/>, where no
    /// such evidence exists.
    /// </summary>
    OutputLimitReached = 10,

    // Refusals: no provider call was made. Never recorded as usage.
    RoleUnavailable = 20,
    InputTooLarge = 21,
    BudgetExhausted = 22,
    ConcurrencyLimited = 23,
    CircuitOpen = 24,
    PolicyUnavailable = 25,

    /// <summary>File-derived detail was about to go to a cloud model without the separate excerpt opt-in.</summary>
    CloudSharingNotAllowed = 26
}

/// <summary>The administrator-tunable bounds on model use. Defaults follow the approved chat limits and are deliberately modest.</summary>
public sealed record ModelExecutionLimits(
    int TimeoutSeconds,
    int MaxInputCharacters,
    int MaxOutputTokens,
    int MaxConcurrentCalls,
    ModelBudget Interactive,
    ModelBudget Background,
    int CloudDailyRequests,
    int CloudDailyTokens)
{
    public const int MinimumTimeoutSeconds = 5;
    public const int MaximumTimeoutSeconds = 120;
    public const int MinimumInputCharacters = 500;
    public const int MaximumInputCharacters = 60_000;
    public const int MinimumOutputTokens = 16;
    public const int MaximumOutputTokens = 2_048;
    public const int MaximumConcurrentCallsLimit = 8;
    public const int MaximumDailyRequests = 100_000;
    public const int MaximumDailyTokens = 100_000_000;

    /// <summary>
    /// Defaults: chat follows the approved 30 AI messages per day per person. Cloud caps default to zero, so allowing
    /// cloud use in settings is not enough on its own: an administrator must also grant a cloud allowance.
    /// </summary>
    public static ModelExecutionLimits Default { get; } = new(
        TimeoutSeconds: 30,
        MaxInputCharacters: 12_000,
        MaxOutputTokens: 512,
        MaxConcurrentCalls: 2,
        Interactive: new ModelBudget(UserDailyRequests: 30, UserDailyTokens: 60_000, InstallationDailyRequests: 300, InstallationDailyTokens: 600_000),
        Background: new ModelBudget(UserDailyRequests: 0, UserDailyTokens: 0, InstallationDailyRequests: 200, InstallationDailyTokens: 400_000),
        CloudDailyRequests: 0,
        CloudDailyTokens: 0);

    /// <summary>
    /// The most of the background installation allowance (requests and tokens) the per-file review roles may use in a day.
    /// Those roles make one call per file or audiobook track, so without a cap a single long audiobook can spend the whole
    /// allowance and starve candidate triage and search guidance, which decide what is downloaded.
    /// </summary>
    public const decimal FileReviewShare = 0.5m;

    public static bool IsFileReviewRole(ModelRole role) => role is ModelRole.ArtifactValidationAssessment or ModelRole.ArtifactValidationDecision;

    /// <summary>At least one unit when the allowance is above zero, so a tiny allowance still permits file review; zero stays a hard stop.</summary>
    public static long FileReviewAllowance(long installationDailyLimit) =>
        installationDailyLimit <= 0 ? 0 : Math.Max(1, (long)(installationDailyLimit * FileReviewShare));

    public ModelBudget BudgetFor(ModelBudgetClass budgetClass) =>
        budgetClass == ModelBudgetClass.InteractiveChat ? Interactive : Background;

    /// <summary>Null when valid; otherwise the first problem, phrased for an administrator.</summary>
    public string? Validate()
    {
        if (TimeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            return $"The timeout must be {MinimumTimeoutSeconds}-{MaximumTimeoutSeconds} seconds.";
        }

        if (MaxInputCharacters is < MinimumInputCharacters or > MaximumInputCharacters)
        {
            return $"The maximum input size must be {MinimumInputCharacters:N0}-{MaximumInputCharacters:N0} characters.";
        }

        if (MaxOutputTokens is < MinimumOutputTokens or > MaximumOutputTokens)
        {
            return $"The maximum output must be {MinimumOutputTokens}-{MaximumOutputTokens:N0} tokens.";
        }

        if (MaxConcurrentCalls is < 1 or > MaximumConcurrentCallsLimit)
        {
            return $"Concurrent calls must be 1-{MaximumConcurrentCallsLimit}.";
        }

        foreach (var value in new[]
        {
            Interactive.UserDailyRequests, Interactive.InstallationDailyRequests, Background.UserDailyRequests,
            Background.InstallationDailyRequests, CloudDailyRequests
        })
        {
            if (value is < 0 or > MaximumDailyRequests)
            {
                return $"Daily request limits must be 0-{MaximumDailyRequests:N0} (0 stops all calls).";
            }
        }

        foreach (var value in new[]
        {
            Interactive.UserDailyTokens, Interactive.InstallationDailyTokens, Background.UserDailyTokens,
            Background.InstallationDailyTokens, CloudDailyTokens
        })
        {
            if (value is < 0 or > MaximumDailyTokens)
            {
                return $"Daily token limits must be 0-{MaximumDailyTokens:N0} (0 stops all calls).";
            }
        }

        return null;
    }
}

/// <summary>A daily allowance. Zero is a hard stop, never "unlimited".</summary>
public sealed record ModelBudget(int UserDailyRequests, int UserDailyTokens, int InstallationDailyRequests, int InstallationDailyTokens);

/// <summary>
/// Fixed diagnostic categories, safe to retain without model or provider text.
/// </summary>
public enum ModelFailureReason
{
    InvalidStructure = 1,
    NoCitedEvidence,
    NoSupportingEvidence,
    NoContradictingEvidence,
    UnknownEvidence,
    DuplicateEvidence,
    UnknownClaim,
    UngroundedVerdict,
    RepeatedSearch,
    InvalidSearch,
    DisallowedAction,
    ExcerptCopied,
    MissingDerivedFact,
    ParserError,
    Cancelled,
    ProviderUnreachable,
    CredentialsRejected,
    ProviderRateLimited,
    ProviderBadRequest,
    ProviderError,
    InvalidProviderResponse,
    UngroundedConcern
}

[Flags]
public enum ModelWorkflowEffect
{
    None = 0,
    NoChange = 1,
    Abstained = 2,
    AdvisorySaved = 4,
    IntentUsed = 8,
    CoverSearchCompleted = 16,
    ExtraSearchCompleted = 32,
    ExtraSearchReturnedResults = 64,
    CandidateVetoUsed = 128,
    CandidateSelectionUsed = 256,
    FileHoldApplied = 512,
    FileReleaseApplied = 1024,
    All = NoChange | Abstained | AdvisorySaved | IntentUsed | CoverSearchCompleted | ExtraSearchCompleted |
        ExtraSearchReturnedResults | CandidateVetoUsed | CandidateSelectionUsed | FileHoldApplied | FileReleaseApplied
}

/// <summary>One reserved/completed model call. Metadata only: never a prompt, reply, excerpt or key.</summary>
public sealed class ModelUsageRecord
{
    private ModelUsageRecord()
    {
    }

    public ModelUsageRecord(
        ModelRole role, ModelBudgetClass budgetClass, Guid? userId, string model, bool isCloud, int reservedTokens, DateTimeOffset occurredAtUtc,
        Guid? mediaAssetId = null)
    {
        Id = Guid.NewGuid();
        Role = role;
        BudgetClass = budgetClass;
        UserId = userId;
        Model = model;
        IsCloud = isCloud;
        ChargedTokens = reservedTokens;
        UsageEstimated = true;
        OccurredAtUtc = occurredAtUtc;
        Outcome = ModelCallOutcome.Pending;
        MediaAssetId = mediaAssetId;
    }

    public Guid Id { get; private set; }

    public ModelRole Role { get; private set; }

    public ModelBudgetClass BudgetClass { get; private set; }

    public Guid? UserId { get; private set; }

    public string Model { get; private set; } = string.Empty;

    public bool IsCloud { get; private set; }

    public ModelCallOutcome Outcome { get; private set; }

    /// <summary>A fixed diagnostic code only. Arbitrary provider/parser text must never enter this ledger.</summary>
    public ModelFailureReason? FailureReason { get; private set; }

    /// <summary>Trusted file reference for diagnosis, never a filename or content. Null for non-file calls and older rows.</summary>
    public Guid? MediaAssetId { get; private set; }

    /// <summary>Monotonic effect flags observed by trusted consumers, not model claims. Null means not observed.</summary>
    public int? WorkflowEffects { get; private set; }

    public void ObserveWorkflow(ModelWorkflowEffect effects)
    {
        if (effects == ModelWorkflowEffect.None || (effects & ~ModelWorkflowEffect.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(effects));
        if (Outcome == ModelCallOutcome.Succeeded) WorkflowEffects = (WorkflowEffects ?? 0) | (int)effects;
    }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public long? LatencyMilliseconds { get; private set; }

    /// <summary>What the provider said it used. Null means it did not say; that is not the same as zero.</summary>
    public int? ReportedPromptTokens { get; private set; }

    public int? ReportedCompletionTokens { get; private set; }

    public int? ReportedTotalTokens { get; private set; }

    /// <summary>What the budget is charged: the reported total when there is one, otherwise a conservative estimate.</summary>
    public int ChargedTokens { get; private set; }

    public bool UsageEstimated { get; private set; }

    public void Complete(
        ModelCallOutcome outcome, long latencyMilliseconds, int? promptTokens, int? completionTokens, int? totalTokens,
        ModelFailureReason? failureReason = null)
    {
        Outcome = outcome;
        FailureReason = outcome is ModelCallOutcome.Succeeded or ModelCallOutcome.Pending ? null : failureReason;
        LatencyMilliseconds = latencyMilliseconds;
        ReportedPromptTokens = promptTokens;
        ReportedCompletionTokens = completionTokens;
        ReportedTotalTokens = totalTokens;
        var known = totalTokens ?? (promptTokens is not null && completionTokens is not null ? promptTokens + completionTokens : null);
        if (known is { } reported)
        {
            ChargedTokens = reported;
            UsageEstimated = false;
        }

        // Otherwise the reservation made before the call stays as the charge: missing usage is never treated as zero.
    }
}
