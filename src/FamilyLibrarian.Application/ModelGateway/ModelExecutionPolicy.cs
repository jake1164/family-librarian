using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

/// <summary>What one governed model call asks for. Contains no tool the model could use to change anything.</summary>
/// <param name="UserId">Who the call is on behalf of. Required for interactive chat; null for background work.</param>
/// <param name="MaxOutputTokens">A tighter output cap than the configured one; never a looser one.</param>
/// <param name="IncludesFileDerivedDetail">
/// True when the messages carry anything read out of a downloaded file (embedded titles, headings, tags, validator text).
/// Such a request is refused for a cloud role unless the separate cloud-excerpt opt-in is on, whatever the caller decided.
/// </param>
public sealed record ModelExecutionRequest(
    ModelRole Role,
    ModelBudgetClass Budget,
    Guid? UserId,
    IReadOnlyList<ModelChatMessage> Messages,
    bool JsonObjectOutput = false,
    int? MaxOutputTokens = null,
    bool IncludesFileDerivedDetail = false,
    Guid? MediaAssetId = null);

/// <summary>The result of interpreting model text. A failure names which kind, so it can be recorded and shown honestly.</summary>
public sealed record ModelParseResult<T>(T? Value, ModelCallOutcome Failure, string? Detail, ModelFailureReason? FailureReason = null)
{
    public bool IsValid => Failure == ModelCallOutcome.Succeeded;
}

public static class ModelParse
{
    public static ModelParseResult<T> Ok<T>(T value) => new(value, ModelCallOutcome.Succeeded, null);

    public static ModelParseResult<T> NotJson<T>(string detail) => new(default, ModelCallOutcome.InvalidJson, detail);

    public static ModelParseResult<T> SchemaRejected<T>(string detail, ModelFailureReason reason = ModelFailureReason.InvalidStructure) =>
        new(default, ModelCallOutcome.SchemaRejected, detail, reason);
}

/// <param name="Usage">What the provider reported, or null when it reported nothing. Null is not zero.</param>
public sealed record ModelExecution<T>(
    ModelCallOutcome Outcome,
    T? Value,
    string? Detail,
    ModelUsage? Usage,
    long LatencyMilliseconds,
    string? Model,
    bool IsCloud,
    ModelFailureReason? FailureReason = null,
    Guid? UsageRecordId = null)
{
    public bool Succeeded => Outcome == ModelCallOutcome.Succeeded;

    /// <summary>True when no provider call was made at all (the policy refused first).</summary>
    public bool WasRefused => Outcome >= ModelCallOutcome.RoleUnavailable;
}

/// <summary>
/// The only way production code may call a model (alpha.6 M4-1). It enforces role availability, size, concurrency, circuit
/// breaker and daily budgets before any call, records every call, and classifies every failure. It never retries, never falls
/// back to another model or placement, and never throws for a provider or policy problem: a failed model call must be a
/// value the caller can ignore, so deterministic behaviour and security holds are unaffected. Only cancellation propagates.
/// </summary>
public interface IModelExecutionPolicy
{
    Task<ModelExecution<T>> ExecuteAsync<T>(
        ModelExecutionRequest request, Func<string, ModelParseResult<T>> parse, CancellationToken cancellationToken);
}

/// <summary>Counts for the current budget window.</summary>
public sealed record ModelUsageTotals(
    int UserRequests, long UserTokens, int InstallationRequests, long InstallationTokens, int CloudRequests, long CloudTokens);

public sealed record ModelUsageSummaryRow(
    ModelRole Role, ModelBudgetClass Budget, ModelCallOutcome Outcome, bool IsCloud, int Calls, long ReportedTokens, int CallsWithoutUsage,
    string Model = "", ModelFailureReason? FailureReason = null, int? WorkflowEffects = null);

public sealed record ModelRecommendationSummaryRow(ModelRole Role, string Model, bool IsCloud, string Verdict, int Findings);

/// <summary>Per-role/outcome counts for the last day. Reported tokens exclude calls where the provider gave no usage.</summary>
public sealed record ModelUsageSummary(DateTimeOffset Since, IReadOnlyList<ModelUsageSummaryRow> Rows,
    IReadOnlyList<ModelRecommendationSummaryRow>? Recommendations = null);

public sealed record ModelCallFailure(
    Guid Id, ModelRole Role, string Model, bool IsCloud, ModelCallOutcome Outcome,
    ModelFailureReason? FailureReason, DateTimeOffset OccurredAtUtc, long? LatencyMilliseconds,
    Guid? MediaAssetId = null, ModelCallArtifact? Artifact = null);

public sealed record ModelCallArtifact(Guid AssetId, Guid WorkId, string Format, Guid? BundleId, int? TrackNumber, int? TrackCount);

public sealed record ModelCallFailures(IReadOnlyList<ModelCallFailure> Rows, bool HasMore);

public sealed record ModelCallDiagnosticFilter(IReadOnlyList<ModelCallOutcome> Outcomes, bool ExcludeOutcomes, bool Cancelled);

public interface IModelUsageLedger
{
    /// <summary>Requests and charged tokens since <paramref name="since"/>, for this user, the installation and cloud. Pending reservations count.</summary>
    Task<ModelUsageTotals> GetTotalsAsync(ModelBudgetClass budget, Guid? userId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Persists a reservation before the provider is called, so a crash cannot make usage vanish.</summary>
    Task AddAsync(ModelUsageRecord record, CancellationToken cancellationToken);

    Task CompleteAsync(Guid recordId, ModelCallOutcome outcome, long latencyMilliseconds, ModelUsage? usage, CancellationToken cancellationToken,
        ModelFailureReason? failureReason = null);

    Task<ModelUsageSummary> SummarizeAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task ObserveWorkflowAsync(Guid recordId, ModelWorkflowEffect effects, CancellationToken cancellationToken);

    Task<ModelCallFailures> ListFailuresAsync(DateTimeOffset since, DateTimeOffset until, ModelRole? role, int page, CancellationToken cancellationToken,
        ModelCallDiagnosticFilter? filter = null);
}

/// <summary>Process-wide state that must outlive a request: the reservation lock and the in-flight counters.</summary>
public sealed class ModelExecutionGate
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, int> perUser = [];
    private int active;

    /// <summary>Serializes "check the budget, then reserve" so two callers cannot both pass the same last unit.</summary>
    public SemaphoreSlim Reservation { get; } = new(1, 1);

    /// <summary>A slot while a call is in flight, or null if the installation (or this person) is already at its limit.</summary>
    public IDisposable? TryEnter(Guid? userId, bool limitPerUser, int maxConcurrent)
    {
        lock (sync)
        {
            if (active >= maxConcurrent || (limitPerUser && userId is { } id && perUser.GetValueOrDefault(id) >= 1))
            {
                return null;
            }

            active++;
            if (limitPerUser && userId is { } user)
            {
                perUser[user] = perUser.GetValueOrDefault(user) + 1;
            }
        }

        return new Slot(this, userId, limitPerUser);
    }

    private void Release(Guid? userId, bool limitPerUser)
    {
        lock (sync)
        {
            active--;
            if (limitPerUser && userId is { } user && perUser.TryGetValue(user, out var count))
            {
                if (count <= 1) perUser.Remove(user); else perUser[user] = count - 1;
            }
        }
    }

    private sealed class Slot(ModelExecutionGate gate, Guid? userId, bool limitPerUser) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release(userId, limitPerUser);
            }
        }
    }
}

/// <summary>
/// Stops calling a model that keeps failing at the provider level. After <see cref="FailureThreshold"/> consecutive provider
/// failures for one endpoint+model it refuses calls for <see cref="OpenFor"/>, then lets a single trial through. Model
/// quality problems (empty text, bad JSON) are not provider failures and never open it.
/// </summary>
public sealed class ModelCircuitBreaker
{
    public const int FailureThreshold = 5;
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(60);

    private readonly object sync = new();
    private readonly Dictionary<string, State> states = [];

    public bool TryAllow(string key, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!states.TryGetValue(key, out var state) || state.OpenUntil is not { } openUntil)
            {
                return true;
            }

            if (now < openUntil || state.TrialInFlight)
            {
                return false;
            }

            state.TrialInFlight = true;
            return true;
        }
    }

    public void RecordSuccess(string key)
    {
        lock (sync)
        {
            states.Remove(key);
        }
    }

    /// <summary>The call ended in a way that says nothing about provider health (for example a bad request).</summary>
    public void RecordNeutral(string key)
    {
        lock (sync)
        {
            if (states.TryGetValue(key, out var state))
            {
                state.TrialInFlight = false;
            }
        }
    }

    public void RecordFailure(string key, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!states.TryGetValue(key, out var state))
            {
                states[key] = state = new State();
            }

            state.Failures++;
            if (state.Failures >= FailureThreshold || state.TrialInFlight)
            {
                state.OpenUntil = now + OpenFor;
            }

            state.TrialInFlight = false;
        }
    }

    private sealed class State
    {
        public int Failures { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }

        public bool TrialInFlight { get; set; }
    }
}

public sealed class ModelExecutionPolicy(
    IModelRoleResolver resolver,
    IModelClient client,
    IModelUsageLedger ledger,
    ModelExecutionGate gate,
    ModelCircuitBreaker breaker,
    IClock clock) : IModelExecutionPolicy
{
    /// <summary>Conservative: real text averages closer to four characters per token, so this over-reserves.</summary>
    private const int CharactersPerTokenEstimate = 3;
    private const int MaxOutputCharactersPerToken = 8;

    /// <summary>One image per call, bounded in size; reserved against the budget as a fixed, generous token cost.</summary>
    public const int MaxImagesPerCall = 1;
    public const int MaxImageBytes = 4 * 1024 * 1024;
    private const int ImageTokenReservation = 1_500;

    public async Task<ModelExecution<T>> ExecuteAsync<T>(
        ModelExecutionRequest request, Func<string, ModelParseResult<T>> parse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parse);

        var resolution = await resolver.ResolveAsync(request.Role, cancellationToken);
        if (!resolution.IsAvailable)
        {
            return Refused<T>(ModelCallOutcome.RoleUnavailable, $"The {request.Role} role is not available ({resolution.Reason}).");
        }

        var images = request.Messages.SelectMany(message => message.Images ?? []).ToArray();
        if (images.Length > MaxImagesPerCall || images.Any(image => image.Bytes.Length is 0 or > MaxImageBytes))
        {
            return Refused<T>(ModelCallOutcome.InputTooLarge, $"At most {MaxImagesPerCall} image of up to {MaxImageBytes / (1024 * 1024)} MB is allowed.");
        }

        // A picture someone sent is as private as a detail read from a file: it never goes to a cloud model without the same opt-in.
        if ((request.IncludesFileDerivedDetail || images.Length > 0) && resolution.IsCloud && !resolution.AllowCloudExcerpts)
        {
            return Refused<T>(ModelCallOutcome.CloudSharingNotAllowed, "Details from files are not allowed to be sent to a cloud model.");
        }

        var limits = resolution.Limits;
        var inputCharacters = request.Messages.Sum(message => message.Content.Length);
        if (request.Messages.Count == 0 || inputCharacters == 0)
        {
            return Refused<T>(ModelCallOutcome.InputTooLarge, "There was nothing to send.");
        }

        if (inputCharacters > limits.MaxInputCharacters)
        {
            return Refused<T>(ModelCallOutcome.InputTooLarge, $"The input is larger than the {limits.MaxInputCharacters:N0}-character limit.");
        }

        var interactive = request.Budget == ModelBudgetClass.InteractiveChat;
        if (interactive && request.UserId is null)
        {
            return Refused<T>(ModelCallOutcome.BudgetExhausted, "Interactive model use needs a person to charge it to.");
        }

        var maxOutputTokens = Math.Min(request.MaxOutputTokens ?? limits.MaxOutputTokens, limits.MaxOutputTokens);
        var breakerKey = $"{resolution.Endpoint}|{resolution.Model}";
        if (!breaker.TryAllow(breakerKey, clock.UtcNow))
        {
            return Refused<T>(ModelCallOutcome.CircuitOpen, "The model has been failing; calls are paused briefly.");
        }

        using var slot = gate.TryEnter(request.UserId, limitPerUser: interactive, limits.MaxConcurrentCalls);
        if (slot is null)
        {
            breaker.RecordNeutral(breakerKey);
            return Refused<T>(ModelCallOutcome.ConcurrencyLimited, "Too many model calls are already running.");
        }

        var estimate = (int)Math.Min(
            int.MaxValue,
            ((long)inputCharacters + CharactersPerTokenEstimate - 1) / CharactersPerTokenEstimate + maxOutputTokens + (long)images.Length * ImageTokenReservation);
        var reservation = new ModelUsageRecord(request.Role, request.Budget, request.UserId, resolution.Model!, resolution.IsCloud, estimate, clock.UtcNow,
            request.MediaAssetId);
        try
        {
            if (await TryReserveAsync(request, resolution, limits, reservation, estimate, cancellationToken) is { } refusal)
            {
                breaker.RecordNeutral(breakerKey);
                return refusal.Outcome == ModelCallOutcome.BudgetExhausted
                    ? Refused<T>(refusal.Outcome, refusal.Detail)
                    : Refused<T>(ModelCallOutcome.PolicyUnavailable, refusal.Detail);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed: if usage cannot be recorded, no call is made.
            breaker.RecordNeutral(breakerKey);
            return Refused<T>(ModelCallOutcome.PolicyUnavailable, "Model usage could not be recorded, so the call was not made.");
        }

        ModelResponse response;
        try
        {
            var connection = new ModelConnection(resolution.Endpoint!, resolution.ApiKey, resolution.Model!, TimeSpan.FromSeconds(limits.TimeoutSeconds));
            response = await client.CompleteAsync(
                connection, new ModelRequest(request.Messages, maxOutputTokens, request.JsonObjectOutput, Reasoning: resolution.Reasoning), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CompleteQuietlyAsync(reservation.Id, ModelCallOutcome.TransportFailure, 0, null, ModelFailureReason.Cancelled);
            breaker.RecordNeutral(breakerKey);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = ModelResponse.Failed(ModelFailureKind.Unreachable, "The model client failed unexpectedly.", 0);
        }

        var execution = Interpret(response, resolution, parse, maxOutputTokens, breaker, breakerKey, clock.UtcNow);
        await CompleteQuietlyAsync(reservation.Id, execution.Outcome, response.LatencyMilliseconds, response.Usage, execution.FailureReason);
        return execution with { UsageRecordId = reservation.Id };
    }

    private static ModelExecution<T> Refused<T>(ModelCallOutcome outcome, string detail) =>
        new(outcome, default, detail, null, 0, null, false);

    private async Task<(ModelCallOutcome Outcome, string Detail)?> TryReserveAsync(
        ModelExecutionRequest request, ModelRoleResolution resolution, ModelExecutionLimits limits,
        ModelUsageRecord reservation, int estimate, CancellationToken cancellationToken)
    {
        await gate.Reservation.WaitAsync(cancellationToken);
        try
        {
            var now = clock.UtcNow;
            var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var totals = await ledger.GetTotalsAsync(request.Budget, request.UserId, dayStart, cancellationToken);
            var budget = limits.BudgetFor(request.Budget);

            if (request.Budget == ModelBudgetClass.InteractiveChat &&
                (totals.UserRequests + 1 > budget.UserDailyRequests || totals.UserTokens + estimate > budget.UserDailyTokens))
            {
                return (ModelCallOutcome.BudgetExhausted, "You have used today's AI allowance.");
            }

            if (totals.InstallationRequests + 1 > budget.InstallationDailyRequests || totals.InstallationTokens + estimate > budget.InstallationDailyTokens)
            {
                return (ModelCallOutcome.BudgetExhausted, "The installation's AI allowance for today has been used.");
            }

            if (resolution.IsCloud && (totals.CloudRequests + 1 > limits.CloudDailyRequests || totals.CloudTokens + estimate > limits.CloudDailyTokens))
            {
                return (ModelCallOutcome.BudgetExhausted, "The cloud allowance for today is zero or has been used.");
            }

            await ledger.AddAsync(reservation, cancellationToken);
            return null;
        }
        finally
        {
            gate.Reservation.Release();
        }
    }

    private static ModelExecution<T> Interpret<T>(
        ModelResponse response, ModelRoleResolution resolution, Func<string, ModelParseResult<T>> parse, int maxOutputTokens,
        ModelCircuitBreaker breaker, string breakerKey, DateTimeOffset now)
    {
        ModelExecution<T> Result(ModelCallOutcome outcome, T? value = default, string? detail = null, ModelFailureReason? reason = null) =>
            new(outcome, value, detail, response.Usage, response.LatencyMilliseconds, resolution.Model, resolution.IsCloud, reason);

        if (!response.Succeeded)
        {
            var outcome = response.Failure switch
            {
                ModelFailureKind.Timeout => ModelCallOutcome.Timeout,
                ModelFailureKind.ModelUnavailable => ModelCallOutcome.ModelUnavailable,
                ModelFailureKind.UnsupportedCapability => ModelCallOutcome.UnsupportedCapability,
                _ => ModelCallOutcome.TransportFailure
            };
            if (response.Failure is ModelFailureKind.BadRequest or ModelFailureKind.UnsupportedCapability)
            {
                breaker.RecordNeutral(breakerKey);
            }
            else
            {
                breaker.RecordFailure(breakerKey, now);
            }

            return Result(outcome, detail: response.FailureDetail, reason: response.Failure switch
            {
                ModelFailureKind.Unreachable => ModelFailureReason.ProviderUnreachable,
                ModelFailureKind.Unauthorized => ModelFailureReason.CredentialsRejected,
                ModelFailureKind.RateLimited => ModelFailureReason.ProviderRateLimited,
                ModelFailureKind.BadRequest => ModelFailureReason.ProviderBadRequest,
                ModelFailureKind.ServerError => ModelFailureReason.ProviderError,
                ModelFailureKind.InvalidResponse => ModelFailureReason.InvalidProviderResponse,
                _ => null
            });
        }

        breaker.RecordSuccess(breakerKey);

        // The provider said it stopped at the output cap. Whatever text exists is cut off, so it is never parsed or accepted:
        // strict validation is not relaxed because a model ran out of tokens.
        if (response.IsOutputLimited)
        {
            return Result(ModelCallOutcome.OutputLimitReached, detail: "The model reached the output limit before it finished its answer.");
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return Result(ModelCallOutcome.EmptyContent, detail: "The model returned no text.");
        }

        if (response.Content.Length > (long)maxOutputTokens * MaxOutputCharactersPerToken)
        {
            return Result(ModelCallOutcome.OutputTooLarge, detail: "The reply was larger than the allowed output size.");
        }

        ModelParseResult<T> parsed;
        try
        {
            parsed = parse(response.Content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(ModelCallOutcome.SchemaRejected, detail: "The reply could not be interpreted.", reason: ModelFailureReason.ParserError);
        }

        return parsed.IsValid ? Result(ModelCallOutcome.Succeeded, parsed.Value) : Result(parsed.Failure, detail: parsed.Detail, reason: parsed.FailureReason);
    }

    private async Task CompleteQuietlyAsync(Guid recordId, ModelCallOutcome outcome, long latency, ModelUsage? usage, ModelFailureReason? reason)
    {
        try
        {
            await ledger.CompleteAsync(recordId, outcome, latency, usage, CancellationToken.None, reason);
        }
        catch (Exception)
        {
            // The reservation already counts against the budget at its conservative estimate; losing the completion is safe.
        }
    }
}
