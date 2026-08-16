using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Policy;

namespace FamilyLibrarian.Application.Policy;

/// <summary>The administrative commands behind the acquisition-policy settings panel, mirroring <c>CwaSettingsService</c>'s shape.</summary>
public sealed class AcquisitionPolicyService(
    IAcquisitionPolicySettingsStore store,
    IPolicyProfileRegistry registry,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// The profile that governs recommendations right now. Currently always
    /// the system default — this is the seam where a future per-user override
    /// would be consulted first, without callers needing to change.
    /// </summary>
    public async Task<string> GetEffectiveProfileIdAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return settings?.DefaultProfileId ?? PolicyProfileIds.ManualChoice;
    }

    public async Task<AcquisitionPolicySettingsStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await store.FindAsync(cancellationToken);
        return ToStatus(settings);
    }

    public async Task<AcquisitionPolicyCommandResult> SetDefaultProfileAsync(
        string profileId, CancellationToken cancellationToken)
    {
        if (registry.Find(profileId) is null)
        {
            return AcquisitionPolicyCommandResult.Invalid("That is not a known acquisition-policy profile.");
        }

        var settings = await store.GetOrCreateAsync(cancellationToken);
        settings.SetDefaultProfile(profileId, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.AcquisitionPolicyDefaultChanged,
            AuditSubjectTypes.AcquisitionPolicy,
            profileId,
            new { ProfileId = profileId },
            cancellationToken);

        return AcquisitionPolicyCommandResult.Success(ToStatus(settings));
    }

    private static AcquisitionPolicySettingsStatus ToStatus(AcquisitionPolicySettings? settings) =>
        new(settings?.DefaultProfileId ?? PolicyProfileIds.ManualChoice, settings?.UpdatedAtUtc);
}

public sealed record AcquisitionPolicySettingsStatus(string DefaultProfileId, DateTimeOffset? UpdatedAtUtc);

public sealed record AcquisitionPolicyCommandResult(
    AcquisitionPolicyCommandOutcome Outcome, AcquisitionPolicySettingsStatus? Status, string? Error)
{
    public static AcquisitionPolicyCommandResult Success(AcquisitionPolicySettingsStatus status) =>
        new(AcquisitionPolicyCommandOutcome.Success, status, null);

    public static AcquisitionPolicyCommandResult Invalid(string error) =>
        new(AcquisitionPolicyCommandOutcome.Invalid, null, error);
}

public enum AcquisitionPolicyCommandOutcome
{
    Success,
    Invalid
}
