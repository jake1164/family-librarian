namespace FamilyLibrarian.Application.Accounts;

/// <summary>
/// Deployment-tunable invitation rules.
/// </summary>
/// <remarks>
/// A plain settings object rather than <c>IOptions</c>: the application layer
/// deliberately takes no dependency on the options packages, so the host binds
/// configuration and supplies an instance.
/// </remarks>
public sealed class InvitationPolicy
{
    public const string SectionName = "Invitations";

    public const int MinimumLifetimeDays = 1;
    public const int MaximumLifetimeDays = 90;
    public const int DefaultLifetimeDays = 7;

    public const int DefaultRedemptionAttemptsPerMinute = 10;

    /// <summary>How long a newly issued invitation stays usable, in days.</summary>
    public int LifetimeDays { get; set; } = DefaultLifetimeDays;

    /// <summary>
    /// Redemption and preview attempts allowed per caller per minute.
    /// </summary>
    /// <remarks>
    /// Configurable because the endpoint is anonymous and the right ceiling
    /// depends on the deployment: a household behind one NAT address shares a
    /// bucket, and an automated test suite hammers it from a single address.
    /// </remarks>
    public int RedemptionAttemptsPerMinute { get; set; } = DefaultRedemptionAttemptsPerMinute;

    public TimeSpan Lifetime => TimeSpan.FromDays(LifetimeDays);

    public bool IsValid =>
        LifetimeDays is >= MinimumLifetimeDays and <= MaximumLifetimeDays &&
        RedemptionAttemptsPerMinute is >= 1 and <= 10_000;
}
