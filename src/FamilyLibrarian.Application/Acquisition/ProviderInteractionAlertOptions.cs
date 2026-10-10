namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Deployment-tunable settings for HUMAN-ACQ-1's Matrix notify/authorize alert.
/// </summary>
/// <remarks>
/// A plain settings object rather than <c>IOptions</c>, mirroring
/// <see cref="ManualImportPolicy"/>: the application layer takes no dependency
/// on the options packages, so the host binds configuration (section
/// <c>Interaction</c>) and supplies an instance. See
/// <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> WP2 for the startup
/// validation this pairs with (in <c>FamilyLibrarian.Web</c>, since it needs
/// <c>RemoteView:AllowedOrigins</c> too).
/// </remarks>
public sealed class ProviderInteractionAlertOptions
{
    public const string SectionName = "Interaction";

    /// <summary>
    /// The externally reachable origin (e.g. <c>https://fl.example.com</c>) magic
    /// links point at. No path, query, or fragment. Unset disables the alert
    /// entirely — there is no way to build a usable link without it.
    /// </summary>
    public string? PublicOrigin { get; set; }

    /// <summary>How long a job must have been waiting before its provider becomes alert-eligible.</summary>
    public int SendDelaySeconds { get; set; } = 45;

    /// <summary>How long a provider must show no job leaving Waiting before a new alert may open (drain suppression).</summary>
    public int QuiescenceSeconds { get; set; } = 240;

    /// <summary>How long a claim may go without a connected viewer before it lapses.</summary>
    public int ClaimLeaseSeconds { get; set; } = 180;

    /// <summary>An open alert older than this is force-expired regardless of state.</summary>
    public int AlertMaxAgeHours { get; set; } = 24;

    /// <summary>How often the alert worker's hosted service runs a pass.</summary>
    public int PassIntervalSeconds { get; set; } = 15;

    /// <summary>A recipient's send is stopped being retried once it has failed this many times.</summary>
    public int MaxSendAttempts { get; set; } = 5;

    public TimeSpan SendDelay => TimeSpan.FromSeconds(SendDelaySeconds);

    public TimeSpan Quiescence => TimeSpan.FromSeconds(QuiescenceSeconds);

    public TimeSpan ClaimLease => TimeSpan.FromSeconds(ClaimLeaseSeconds);

    public TimeSpan AlertMaxAge => TimeSpan.FromHours(AlertMaxAgeHours);

    public TimeSpan PassInterval => TimeSpan.FromSeconds(PassIntervalSeconds);

    /// <summary>Whether alerts can be sent at all — false until an operator sets <see cref="PublicOrigin"/>.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(PublicOrigin);
}
