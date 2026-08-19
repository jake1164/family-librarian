namespace FamilyLibrarian.Domain.Policy;

/// <summary>
/// The household's system-wide default acquisition-policy profile.
/// </summary>
/// <remarks>
/// One row, created on first configuration — same singleton-row shape as
/// <c>CwaSettings</c>/<c>AudiobookshelfSettings</c>. Defaults to
/// <see cref="PolicyProfileIds.ManualChoice"/> (no auto-recommendation) so a
/// fresh install behaves exactly as it did before this feature existed, until
/// an admin opts into a ranking profile.
/// </remarks>
public sealed class AcquisitionPolicySettings
{
    private AcquisitionPolicySettings()
    {
    }

    public AcquisitionPolicySettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        DefaultProfileId = PolicyProfileIds.ManualChoice;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string DefaultProfileId { get; private set; } = PolicyProfileIds.ManualChoice;

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void SetDefaultProfile(string profileId, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("A profile id is required.", nameof(profileId));
        }

        DefaultProfileId = profileId;
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }
}
