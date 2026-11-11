using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.DecisionCapture;

public sealed record DecisionCaptureSettingsView(bool DecisionCaptureEnabled, bool ChatCaptureEnabled, DecisionCaptureLimits Limits);

/// <param name="Limits">The new limits; required, because a partial update could not be range-checked as a whole.</param>
public sealed record SaveDecisionCaptureSettings(bool DecisionCaptureEnabled, bool ChatCaptureEnabled, DecisionCaptureLimits Limits);

public sealed record DecisionCaptureResult<T>(bool Succeeded, bool Forbidden, string? Error, T? Value);

public static class DecisionCaptureResult
{
    public static DecisionCaptureResult<T> Success<T>(T value) => new(true, false, null, value);

    public static DecisionCaptureResult<T> Invalid<T>(string error) => new(false, false, error, default);

    public static DecisionCaptureResult<T> NotAllowed<T>() => new(false, true, "Administrator access is required.", default);
}

/// <summary>
/// Administrator control of decision capture: the two switches, the retention limits, a count of what lowering a limit would
/// remove, the purge, and the counts-and-sizes insight. Each operation checks for an administrator itself, not only the route.
/// Nothing here makes a model call, so every operation works whether AI is on or off, and a purge works even when AI is off.
/// </summary>
public sealed class DecisionCaptureService(
    IModelGatewaySettingsStore settingsStore,
    IDecisionCaptureStore captureStore,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ICurrentAdministrator administrator,
    IClock clock)
{
    private const string SubjectId = "decision-capture";

    public async Task<DecisionCaptureResult<DecisionCaptureSettingsView>> GetAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return DecisionCaptureResult.NotAllowed<DecisionCaptureSettingsView>();
        }

        return DecisionCaptureResult.Success<DecisionCaptureSettingsView>(ToView(await settingsStore.FindAsync(cancellationToken)));
    }

    public async Task<DecisionCaptureResult<DecisionCaptureSettingsView>> SaveAsync(
        SaveDecisionCaptureSettings command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdministrator())
        {
            return DecisionCaptureResult.NotAllowed<DecisionCaptureSettingsView>();
        }

        if (command.Limits is null || command.Limits.Validate() is { } problem)
        {
            return DecisionCaptureResult.Invalid<DecisionCaptureSettingsView>(command.Limits?.Validate() ?? "The limits are required.");
        }

        var settings = await settingsStore.GetOrCreateAsync(cancellationToken);
        var before = ToView(settings);
        settings.ConfigureDecisionCapture(command.DecisionCaptureEnabled, command.ChatCaptureEnabled, command.Limits, currentUser.UserId, clock.UtcNow);
        await settingsStore.SaveChangesAsync(cancellationToken);
        var after = ToView(settings);
        await audit.WriteAsync(
            AuditActions.DecisionCaptureSettingsChanged, AuditSubjectTypes.ModelGateway, SubjectId,
            new { Before = before, After = after }, cancellationToken);
        return DecisionCaptureResult.Success<DecisionCaptureSettingsView>(after);
    }

    /// <summary>How many records the next retention pass would remove if these limits were saved. A count only; nothing changes.</summary>
    public async Task<DecisionCaptureResult<DecisionCaptureRetentionCounts>> PreviewAsync(
        DecisionCaptureLimits limits, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return DecisionCaptureResult.NotAllowed<DecisionCaptureRetentionCounts>();
        }

        if (limits is null || limits.Validate() is { } problem)
        {
            return DecisionCaptureResult.Invalid<DecisionCaptureRetentionCounts>(limits?.Validate() ?? "The limits are required.");
        }

        return DecisionCaptureResult.Success<DecisionCaptureRetentionCounts>(
            await captureStore.CountRetentionAsync(limits, clock.UtcNow, cancellationToken));
    }

    public async Task<DecisionCaptureResult<DecisionCaptureInsight>> InsightAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return DecisionCaptureResult.NotAllowed<DecisionCaptureInsight>();
        }

        return DecisionCaptureResult.Success<DecisionCaptureInsight>(await captureStore.GetInsightAsync(clock.UtcNow, cancellationToken));
    }

    /// <param name="days">1, 7, 30 or 90: the ledger keeps 90 days, so that is as far back as "what happened next" can reach.</param>
    public async Task<DecisionCaptureResult<IReadOnlyList<DecisionCaptureEffectivenessRow>>> EffectivenessAsync(int days, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return DecisionCaptureResult.NotAllowed<IReadOnlyList<DecisionCaptureEffectivenessRow>>();
        }

        if (days is not (1 or 7 or 30 or 90))
        {
            return DecisionCaptureResult.Invalid<IReadOnlyList<DecisionCaptureEffectivenessRow>>("Choose 1, 7, 30 or 90 days.");
        }

        return DecisionCaptureResult.Success(await captureStore.GetEffectivenessAsync(clock.UtcNow.AddDays(-days), cancellationToken));
    }

    /// <param name="role">One scored role, or null for all of them.</param>
    public async Task<DecisionCaptureResult<DecisionCapturePurgeResult>> PurgeAsync(ModelRole? role, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return DecisionCaptureResult.NotAllowed<DecisionCapturePurgeResult>();
        }

        if (role is { } selected && !DecisionCaptureRoles.Scored.Contains(selected))
        {
            return DecisionCaptureResult.Invalid<DecisionCapturePurgeResult>("Choose one of the AI features that are captured.");
        }

        var purged = await captureStore.PurgeAsync(role, cancellationToken);
        await audit.WriteAsync(
            AuditActions.DecisionCapturePurged, AuditSubjectTypes.ModelGateway, SubjectId,
            new { Role = role?.ToString() ?? "All", purged.Snapshots, purged.Calls, purged.Outcomes, purged.CandidateLinks, purged.UsageRollups }, cancellationToken);
        return DecisionCaptureResult.Success<DecisionCapturePurgeResult>(purged);
    }

    internal static DecisionCaptureSettingsView ToView(ModelGatewaySettings? settings) => settings is null
        ? new DecisionCaptureSettingsView(true, false, DecisionCaptureLimits.Default)
        : new DecisionCaptureSettingsView(settings.DecisionCaptureEnabled, settings.ChatCaptureEnabled, settings.CaptureLimits);

    private bool IsAdministrator() => currentUser.UserId is not null && administrator.IsAdministrator;
}
