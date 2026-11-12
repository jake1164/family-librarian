using System.Collections.Concurrent;
using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.ModelTesting;

namespace FamilyLibrarian.Application.ModelTesting;

public enum ModelTestTarget
{
    Primary,
    Comparison,
    Cloud,

    /// <summary>The model a production role is configured to use, looked up read-only. Cloud-ness comes from the role's placement.</summary>
    Role,

    /// <summary>A test-only alias the administrator typed. Never assumed local: the run must say where it runs.</summary>
    Alias
}

public enum ModelCheck
{
    /// <summary>Transport only: the endpoint answered with a well-formed completion. Says nothing about whether the model produced visible text.</summary>
    Connectivity,

    /// <summary>The model produced usable visible text within the output limit. Reported separately from <see cref="Connectivity"/>.</summary>
    AssistantContent,
    IntentClassification,
    StructuredJson,
    MatchAdvisory,
    MalformedResponses,
    ModelUnavailable,
    ToolCapability
}

public enum ModelCheckStatus
{
    Passed,
    Failed,
    NotSupported,
    Error,
    Skipped
}

public sealed record ModelCheckResult(
    ModelCheck Check,
    ModelCheckStatus Status,
    string Detail,
    int Calls,
    long LatencyMilliseconds,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    string? Sample,
    bool OutputLimitReached = false);

/// <summary>What one run observed. Informational only: nothing in Family Librarian acts on it.</summary>
public sealed record ModelTestReport(
    ModelTestTarget Target,
    bool IsCloud,
    string ConfiguredModel,
    IReadOnlyList<string> ReportedModels,
    IReadOnlyList<ModelCheckResult> Results,
    int TotalCalls,
    int? TotalTokens,
    long TotalLatencyMilliseconds,
    ModelReasoningMode Reasoning = ModelReasoningMode.Inherit,
    int MaxOutputTokens = 0);

public enum ModelTestRunOutcome
{
    Completed,
    Forbidden,
    Disabled,
    NotConfigured,
    CloudNotPermitted,
    Busy,
    Invalid
}

/// <param name="Reasoning">This run's reasoning choice; null uses the test setting. Never changes any stored setting.</param>
/// <param name="TimeoutSeconds">This run's timeout; null uses the test setting.</param>
/// <param name="MaxOutputTokens">This run's output cap; null uses the test setting.</param>
/// <param name="Role">Required for <see cref="ModelTestTarget.Role"/>: whose configured model to test.</param>
/// <param name="Alias">Required for <see cref="ModelTestTarget.Alias"/>: a test-only model alias.</param>
/// <param name="AliasIsCloud">Required for <see cref="ModelTestTarget.Alias"/>: whether that alias is a cloud model. A name is never trusted to say.</param>
public sealed record ModelTestRunRequest(
    ModelTestTarget Target, IReadOnlyList<ModelCheck>? Checks, bool ConfirmCloud,
    ModelReasoningMode? Reasoning = null, int? TimeoutSeconds = null, int? MaxOutputTokens = null,
    ModelRole? Role = null, string? Alias = null, bool? AliasIsCloud = null);

public sealed record ModelTestRunResult(ModelTestRunOutcome Outcome, string? Error, ModelTestReport? Report)
{
    public static ModelTestRunResult Refused(ModelTestRunOutcome outcome, string error) => new(outcome, error, null);
}

/// <summary>At most one test run per administrator at a time. Counters keyed by user id only; never an identity source.</summary>
public sealed class ModelTestGate
{
    private readonly ConcurrentDictionary<Guid, byte> running = new();

    public IDisposable? TryEnter(Guid administratorId) =>
        running.TryAdd(administratorId, 0) ? new Lease(this, administratorId) : null;

    private sealed class Lease(ModelTestGate owner, Guid administratorId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.running.TryRemove(administratorId, out _);
            }
        }
    }
}

/// <summary>
/// The manual model connectivity and capability test (alpha.6 M3-5). An administrator runs it on demand against one
/// explicitly chosen model; it uses only fictional text, calls the model with a fixed, small, bounded set of prompts, never
/// retries, never falls back to another model and never runs in the background. Every reply is untrusted data: it is
/// validated strictly and reported, not obeyed. Nothing here can create a request, change matching, approve or publish, and
/// the raw prompts and replies are not stored (only a short excerpt is returned to the administrator who asked).
/// </summary>
public sealed class ModelCapabilityTestService(
    IModelTestSettingsStore store,
    IModelTestTargetSource targets,
    IModelClient client,
    ModelTestGate gate,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ICurrentAdministrator administrator)
{
    private const string NoSuchModel = "fl-capability-test-no-such-model";
    private const int SampleLength = 300;

    private static readonly ModelCheck[] AllChecks = Enum.GetValues<ModelCheck>();

    public async Task<ModelTestRunResult> RunAsync(ModelTestRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentUser.UserId is not { } administratorId || !administrator.IsAdministrator)
        {
            return ModelTestRunResult.Refused(ModelTestRunOutcome.Forbidden, "Administrator access is required.");
        }

        if (!Enum.IsDefined(request.Target) || request.Checks?.Any(check => !Enum.IsDefined(check)) == true)
        {
            return ModelTestRunResult.Refused(ModelTestRunOutcome.Invalid, "Choose a valid model and checks.");
        }

        var settings = await store.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true })
        {
            return ModelTestRunResult.Refused(ModelTestRunOutcome.Disabled, "Model testing is turned off.");
        }

        string? model;
        bool isCloud;
        switch (request.Target)
        {
            case ModelTestTarget.Primary:
                (model, isCloud) = (settings.PrimaryModel, false);
                break;
            case ModelTestTarget.Comparison:
                (model, isCloud) = (settings.ComparisonModel, false);
                break;
            case ModelTestTarget.Cloud:
                (model, isCloud) = (settings.CloudModel, true);
                break;
            case ModelTestTarget.Role:
                if (request.Role is not { } role || !Enum.IsDefined(role))
                {
                    return ModelTestRunResult.Refused(ModelTestRunOutcome.Invalid, "Choose which role's model to test.");
                }

                var roleTarget = await targets.FindRoleTargetAsync(role, cancellationToken);
                if (roleTarget is null)
                {
                    return ModelTestRunResult.Refused(ModelTestRunOutcome.NotConfigured, "That role has no model alias configured.");
                }

                (model, isCloud) = (roleTarget.Alias, roleTarget.IsCloud);
                break;
            default:
                if (string.IsNullOrWhiteSpace(request.Alias) || !ModelConfigurationRules.IsSafeModelName(request.Alias.Trim()))
                {
                    return ModelTestRunResult.Refused(ModelTestRunOutcome.Invalid, "Enter a model alias using letters, digits and . _ - : / @ only.");
                }

                if (request.AliasIsCloud is not { } declaredCloud)
                {
                    return ModelTestRunResult.Refused(ModelTestRunOutcome.Invalid, "Say whether that alias runs locally or in the cloud; a name is never assumed to be local.");
                }

                (model, isCloud) = (request.Alias.Trim(), declaredCloud);
                break;
        }

        // Cloud testing has its own switch plus a per-run confirmation, independent of whether any production role may use a cloud model.
        if (isCloud && !(settings.AllowCloud && request.ConfirmCloud))
        {
            return ModelTestRunResult.Refused(
                ModelTestRunOutcome.CloudNotPermitted,
                "Cloud tests need cloud testing switched on in the test settings and an explicit confirmation on this run.");
        }

        var connectionInfo = await targets.GetConnectionAsync(cancellationToken);
        if (connectionInfo.Endpoint is not { } endpoint || model is null)
        {
            return ModelTestRunResult.Refused(
                ModelTestRunOutcome.NotConfigured,
                connectionInfo.Problem ?? "The endpoint and the chosen model alias must be configured first.");
        }

        var apiKey = connectionInfo.ApiKey;

        using var lease = gate.TryEnter(administratorId);
        if (lease is null)
        {
            return ModelTestRunResult.Refused(ModelTestRunOutcome.Busy, "A model test is already running for you. Wait for it to finish.");
        }

        var timeoutSeconds = request.TimeoutSeconds ?? settings.TimeoutSeconds;
        var maxOutputTokens = request.MaxOutputTokens ?? settings.MaxOutputTokens;
        var reasoning = request.Reasoning ?? settings.Reasoning;
        if (timeoutSeconds is < ModelTestSettings.MinimumTimeoutSeconds or > ModelTestSettings.MaximumTimeoutSeconds ||
            maxOutputTokens is < ModelTestSettings.MinimumMaxOutputTokens or > ModelTestSettings.MaximumMaxOutputTokens ||
            !Enum.IsDefined(reasoning))
        {
            return ModelTestRunResult.Refused(ModelTestRunOutcome.Invalid, "The timeout, output limit or reasoning choice for this run is out of range.");
        }

        var connection = new ModelConnection(endpoint, apiKey, model, TimeSpan.FromSeconds(timeoutSeconds));
        var checks = (request.Checks is { Count: > 0 } ? request.Checks : AllChecks).Distinct().Order().ToArray();
        var run = new Run(client, connection, maxOutputTokens, reasoning, cancellationToken);
        var results = new List<ModelCheckResult>();
        foreach (var check in checks)
        {
            results.Add(await RunCheckAsync(check, run, isCloud));
        }

        var report = new ModelTestReport(
            request.Target, isCloud, model, run.ReportedModels.Order(StringComparer.Ordinal).ToArray(), results, run.Calls,
            run.TotalTokens, run.TotalLatency, reasoning, maxOutputTokens);

        // Outcomes and usage only: never the prompts or the model's text.
        await audit.WriteAsync(
            AuditActions.ModelTestRun, AuditSubjectTypes.ModelTest, "model-test",
            new
            {
                Target = request.Target.ToString(),
                Model = model,
                IsCloud = isCloud,
                Reasoning = reasoning.ToString(),
                MaxOutputTokens = maxOutputTokens,
                TimeoutSeconds = timeoutSeconds,
                report.TotalCalls,
                report.TotalTokens,
                report.TotalLatencyMilliseconds,
                Results = results.Select(result => new { Check = result.Check.ToString(), Status = result.Status.ToString(), result.Calls, result.OutputLimitReached }).ToArray()
            },
            cancellationToken);

        return new ModelTestRunResult(ModelTestRunOutcome.Completed, null, report);
    }

    private static async Task<ModelCheckResult> RunCheckAsync(ModelCheck check, Run run, bool isCloud)
    {
        var scope = run.BeginCheck();
        try
        {
            if (check == ModelCheck.MalformedResponses)
            {
                var ok = ModelTestCases.RunMalformedResponseSelfTest();
                return scope.Finish(
                    check, ok ? ModelCheckStatus.Passed : ModelCheckStatus.Failed,
                    ok ? "Every malformed reply was rejected and a correct one was accepted (offline check, no model call)."
                       : "The strict validator accepted a malformed reply or rejected a correct one.",
                    sample: null);
            }

            if (check == ModelCheck.ModelUnavailable && isCloud)
            {
                return scope.Finish(check, ModelCheckStatus.Skipped, "Not run against a cloud model: the probe uses a made-up alias and is only done locally.", null);
            }

            if (run.Aborted is { } reason)
            {
                return scope.Finish(check, ModelCheckStatus.Skipped, reason, null);
            }

            return check switch
            {
                ModelCheck.Connectivity => await ConnectivityAsync(scope),
                ModelCheck.AssistantContent => await AssistantContentAsync(scope),
                ModelCheck.IntentClassification => await IntentAsync(scope),
                ModelCheck.StructuredJson => await StructuredJsonAsync(scope),
                ModelCheck.MatchAdvisory => await MatchAdvisoryAsync(scope),
                ModelCheck.ModelUnavailable => await ModelUnavailableAsync(scope),
                ModelCheck.ToolCapability => await ToolCapabilityAsync(scope),
                _ => scope.Finish(check, ModelCheckStatus.Skipped, "Unknown check.", null)
            };
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>Transport and credentials only. A well-formed reply with no visible text is still a working connection.</summary>
    private const int ConnectivityOutputTokens = 16;

    private static async Task<ModelCheckResult> ConnectivityAsync(CheckScope scope)
    {
        var response = await scope.CallAsync(ModelTestCases.ConnectivityRequest(ConnectivityOutputTokens));
        if (!response.Succeeded)
        {
            return scope.FailedCall(ModelCheck.Connectivity, response);
        }

        var detail = response.IsOutputLimited
            ? "Connected. The endpoint answered, but stopped at the small output limit this probe uses; see the next check for whether the model gives usable text."
            : string.IsNullOrWhiteSpace(response.Content)
                ? "Connected. The endpoint answered with no visible text; see the next check."
                : "Connected. The endpoint accepted the key and answered.";
        return scope.Finish(ModelCheck.Connectivity, ModelCheckStatus.Passed, detail, null);
    }

    /// <summary>Whether the model produces usable visible text within the configured output limit, so thinking-model exhaustion is visible.</summary>
    private static async Task<ModelCheckResult> AssistantContentAsync(CheckScope scope)
    {
        var response = await scope.CallAsync(ModelTestCases.ConnectivityRequest(scope.MaxOutputTokens), abortOnFailure: false);
        if (!response.Succeeded)
        {
            return scope.FailedCall(ModelCheck.AssistantContent, response);
        }

        if (response.IsOutputLimited)
        {
            return scope.Finish(
                ModelCheck.AssistantContent, ModelCheckStatus.Failed,
                "The model reached the output limit before it finished. A thinking model may be spending the limit on hidden reasoning: raise the limit, or turn reasoning off if the model supports it.",
                response.Content);
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return scope.Finish(ModelCheck.AssistantContent, ModelCheckStatus.Failed, "The model answered with no visible text, and did not say it hit the output limit.", null);
        }

        var exact = string.Equals(response.Content.Trim().Trim('.', '!', '"').Trim(), "pong", StringComparison.OrdinalIgnoreCase);
        return scope.Finish(
            ModelCheck.AssistantContent, ModelCheckStatus.Passed,
            $"Replied with visible text ({(exact ? "followed the exact-reply instruction" : "did not follow the exact-reply instruction")}).", response.Content);
    }

    private static async Task<ModelCheckResult> IntentAsync(CheckScope scope)
    {
        var valid = 0;
        var agreed = 0;
        string? lastSample = null;
        string? problem = null;
        foreach (var (message, expected) in ModelTestCases.IntentCases)
        {
            var response = await scope.CallAsync(ModelTestCases.IntentRequest(message, scope.MaxOutputTokens));
            if (!response.Succeeded)
            {
                return scope.FailedCall(ModelCheck.IntentClassification, response);
            }

            lastSample = response.Content;
            if (response.IsOutputLimited)
            {
                problem ??= "the output limit was reached";
                continue;
            }

            var parsed = ModelOutputValidator.ValidateIntent(response.Content);
            if (!parsed.IsValid)
            {
                problem ??= parsed.Error;
                continue;
            }

            valid++;
            if (parsed.Value == expected) agreed++;
        }

        var total = ModelTestCases.IntentCases.Count;
        return valid == total
            ? scope.Finish(ModelCheck.IntentClassification, ModelCheckStatus.Passed,
                string.Create(CultureInfo.InvariantCulture, $"All {total} replies had the required shape; {agreed} of {total} matched the expected label (informational, not an accuracy score)."), lastSample)
            : scope.Finish(ModelCheck.IntentClassification, ModelCheckStatus.Failed,
                string.Create(CultureInfo.InvariantCulture, $"{total - valid} of {total} replies were rejected: {problem}."), lastSample);
    }

    private static async Task<ModelCheckResult> StructuredJsonAsync(CheckScope scope)
    {
        var response = await scope.CallAsync(ModelTestCases.StructuredJsonRequest(scope.MaxOutputTokens));
        if (!response.Succeeded)
        {
            return scope.FailedCall(ModelCheck.StructuredJson, response);
        }

        if (response.IsOutputLimited)
        {
            return scope.Finish(ModelCheck.StructuredJson, ModelCheckStatus.Failed, "The output limit was reached before the JSON was complete; the cut-off reply was not accepted.", response.Content);
        }

        var parsed = ModelOutputValidator.ValidateBookFacts(response.Content);
        if (!parsed.IsValid)
        {
            return scope.Finish(ModelCheck.StructuredJson, ModelCheckStatus.Failed, $"Reply rejected: {parsed.Error}.", response.Content);
        }

        var facts = parsed.Value!;
        var correct = facts.Year == ModelTestCases.FactsYear && string.Equals(facts.Title, ModelTestCases.FactsTitle, StringComparison.OrdinalIgnoreCase);
        return scope.Finish(ModelCheck.StructuredJson, ModelCheckStatus.Passed,
            $"Valid JSON in the exact shape; the values {(correct ? "match" : "do not match")} the fictional source text (informational).", response.Content);
    }

    private static async Task<ModelCheckResult> MatchAdvisoryAsync(CheckScope scope)
    {
        var valid = 0;
        var agreed = 0;
        string? sample = null;
        string? problem = null;
        foreach (var scenario in ModelTestCases.MatchScenarios)
        {
            var response = await scope.CallAsync(ModelTestCases.MatchAdvisoryRequest(scenario, scope.MaxOutputTokens));
            if (!response.Succeeded)
            {
                return scope.FailedCall(ModelCheck.MatchAdvisory, response);
            }

            sample = response.Content;
            if (response.IsOutputLimited)
            {
                problem ??= "the output limit was reached";
                continue;
            }

            var parsed = ModelOutputValidator.ValidateMatchAdvisory(response.Content, scenario.EvidenceIds);
            if (!parsed.IsValid)
            {
                problem ??= parsed.Error;
                continue;
            }

            valid++;
            if (scenario.Acceptable.Contains(parsed.Value!.Assessment, StringComparer.Ordinal)) agreed++;
        }

        var total = ModelTestCases.MatchScenarios.Count;
        return valid == total
            ? scope.Finish(ModelCheck.MatchAdvisory, ModelCheckStatus.Passed,
                string.Create(CultureInfo.InvariantCulture, $"All {total} advisory replies were valid and cited only supplied evidence; {agreed} of {total} agreed with the fixture (advisory only: no decision is made from it)."), sample)
            : scope.Finish(ModelCheck.MatchAdvisory, ModelCheckStatus.Failed,
                string.Create(CultureInfo.InvariantCulture, $"{total - valid} of {total} advisory replies were rejected: {problem}."), sample);
    }

    private static async Task<ModelCheckResult> ModelUnavailableAsync(CheckScope scope)
    {
        var response = await scope.CallAsync(ModelTestCases.ConnectivityRequest(16), modelOverride: NoSuchModel, abortOnFailure: false);
        if (response.Succeeded)
        {
            return scope.Finish(ModelCheck.ModelUnavailable, ModelCheckStatus.Failed,
                "A made-up model alias was answered as if it existed; the endpoint may be substituting a different model.", response.Content);
        }

        var classified = response.Failure is ModelFailureKind.ModelUnavailable or ModelFailureKind.BadRequest;
        return scope.Finish(ModelCheck.ModelUnavailable, classified ? ModelCheckStatus.Passed : ModelCheckStatus.Error,
            $"A made-up alias was refused and classified as {response.Failure}.", response.FailureDetail);
    }

    private static async Task<ModelCheckResult> ToolCapabilityAsync(CheckScope scope)
    {
        var response = await scope.CallAsync(ModelTestCases.ToolRequest(scope.MaxOutputTokens), abortOnFailure: false);
        if (!response.Succeeded)
        {
            return response.Failure is ModelFailureKind.BadRequest or ModelFailureKind.ModelUnavailable
                ? scope.Finish(ModelCheck.ToolCapability, ModelCheckStatus.NotSupported, "The endpoint or model rejected a request that declares tools.", response.FailureDetail)
                : scope.FailedCall(ModelCheck.ToolCapability, response);
        }

        if (response.ToolCalls.Count == 0 && response.IsOutputLimited)
        {
            return scope.Finish(ModelCheck.ToolCapability, ModelCheckStatus.Failed, "The output limit was reached before the model could answer, so tool calling could not be observed.", response.Content);
        }

        if (response.ToolCalls.Count == 0)
        {
            return scope.Finish(ModelCheck.ToolCapability, ModelCheckStatus.NotSupported, "The model answered in text and did not call the declared tool.", response.Content);
        }

        var unexpected = response.ToolCalls.Where(call => call.Name != ModelTestCases.ToolName).Select(call => call.Name).ToArray();
        return unexpected.Length > 0
            ? scope.Finish(ModelCheck.ToolCapability, ModelCheckStatus.Failed, "The model asked for a tool that was never declared; it was ignored.", string.Join(", ", unexpected))
            : scope.Finish(ModelCheck.ToolCapability, ModelCheckStatus.Passed, "The model returned a call to the declared tool. Nothing was executed.", ModelTestCases.ToolName);
    }

    /// <summary>State for one run: the connection, the running totals and the shared decision to stop after an unreachable endpoint.</summary>
    private sealed class Run(IModelClient client, ModelConnection connection, int maxOutputTokens, ModelReasoningMode reasoning, CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken => cancellationToken;

        public int Calls { get; private set; }

        public long TotalLatency { get; private set; }

        public int? TotalTokens { get; private set; }

        public HashSet<string> ReportedModels { get; } = [];

        public string? Aborted { get; private set; }

        public CheckScope BeginCheck() => new(this);

        public async Task<ModelResponse> CallAsync(ModelRequest request, string? modelOverride, bool abortOnFailure)
        {
            var response = await client.CompleteAsync(
                modelOverride is null ? connection : connection with { Model = modelOverride }, request with { Reasoning = reasoning }, cancellationToken);
            Calls++;
            TotalLatency += response.LatencyMilliseconds;
            if (response.Usage?.TotalTokens is { } tokens)
            {
                TotalTokens = (TotalTokens ?? 0) + tokens;
            }

            if (response.ReportedModel is { Length: > 0 } reported)
            {
                ReportedModels.Add(Sanitize(reported, 128));
            }

            // One unreachable, slow or unauthorized endpoint ends the run. No retry, no hammering.
            if (abortOnFailure && response.Failure is ModelFailureKind.Unreachable or ModelFailureKind.Timeout or ModelFailureKind.Unauthorized or ModelFailureKind.ModelUnavailable)
            {
                Aborted = $"Stopped after the previous call failed ({response.Failure}).";
            }

            return response;
        }

        public int MaxOutputTokens => maxOutputTokens;
    }

    private sealed class CheckScope(Run run)
    {
        private int calls;
        private long latency;
        private int? promptTokens;
        private int? completionTokens;
        private int? totalTokens;
        private bool outputLimited;

        public int MaxOutputTokens => run.MaxOutputTokens;

        public async Task<ModelResponse> CallAsync(ModelRequest request, string? modelOverride = null, bool abortOnFailure = true)
        {
            var response = await run.CallAsync(request, modelOverride, abortOnFailure);
            calls++;
            latency += response.LatencyMilliseconds;
            promptTokens = Add(promptTokens, response.Usage?.PromptTokens);
            completionTokens = Add(completionTokens, response.Usage?.CompletionTokens);
            totalTokens = Add(totalTokens, response.Usage?.TotalTokens);
            outputLimited |= response.Succeeded && response.IsOutputLimited;
            return response;
        }

        public ModelCheckResult FailedCall(ModelCheck check, ModelResponse response) =>
            Finish(check, ModelCheckStatus.Error, $"{Describe(response.Failure)}.", response.FailureDetail);

        public ModelCheckResult Finish(ModelCheck check, ModelCheckStatus status, string detail, string? sample) =>
            new(check, status, detail, calls, latency, promptTokens, completionTokens, totalTokens,
                sample is null ? null : Sanitize(sample, SampleLength), outputLimited);

        private static int? Add(int? current, int? next) => next is null ? current : (current ?? 0) + next;
    }

    private static string Describe(ModelFailureKind kind) => kind switch
    {
        ModelFailureKind.Unreachable => "The endpoint could not be reached",
        ModelFailureKind.Timeout => "The model did not answer within the timeout",
        ModelFailureKind.Unauthorized => "The endpoint refused the API key",
        ModelFailureKind.ModelUnavailable => "The endpoint does not know that model alias",
        ModelFailureKind.RateLimited => "The endpoint is rate limiting requests",
        ModelFailureKind.BadRequest => "The endpoint rejected the request",
        ModelFailureKind.UnsupportedCapability => "The model or endpoint does not support a setting this request used (for example JSON mode, tools, or reasoning off)",
        ModelFailureKind.ServerError => "The endpoint reported a server error",
        ModelFailureKind.InvalidResponse => "The endpoint's reply could not be understood",
        _ => "The call failed"
    };

    /// <summary>Plain text, no control characters, bounded. Shown to the administrator only, never stored.</summary>
    internal static string Sanitize(string value, int maxLength)
    {
        var cleaned = new string(value.Select(character => char.IsControl(character) && character != '\n' ? ' ' : character).ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }
}
