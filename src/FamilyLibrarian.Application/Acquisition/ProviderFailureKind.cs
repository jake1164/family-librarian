namespace FamilyLibrarian.Application.Acquisition;

/// <summary>What a provider's job-failure code says about the <em>copy</em>, as opposed to the source.</summary>
public enum ProviderFailureKind
{
    /// <summary>The copy could not be used (or the cause is unknown). The candidate is ruled out and the loop moves on.</summary>
    CopyProblem,

    /// <summary>The source refused or could not be reached on our side (credentials, access rules, VPN). The copy is fine.</summary>
    SourceProblem,

    /// <summary>The source asked us to slow down. The copy is fine.</summary>
    Throttled
}

/// <summary>
/// Classifies a protocol-v2 job error code once, so the retry loop does not rule a good copy out because its
/// <em>source</em> refused the download (lab: an indexer's bot filter refused the provider's client and the only English
/// copy of a book was discarded for good). Unknown and missing codes are copy problems: today's behaviour.
/// </summary>
public static class ProviderFailureClassifier
{
    /// <summary>How long a source problem keeps that provider quiet for the format before it is asked again.</summary>
    public static readonly TimeSpan SourceProblemRecheckDelay = TimeSpan.FromHours(6);

    private static readonly TimeSpan DefaultThrottleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumThrottleDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumThrottleDelay = TimeSpan.FromHours(24);

    public static ProviderFailureKind Classify(string? errorCode) => errorCode switch
    {
        "AUTH_REQUIRED" or "AUTH_FAILED" or "VPN_UNAVAILABLE" => ProviderFailureKind.SourceProblem,
        "RATE_LIMITED" => ProviderFailureKind.Throttled,
        _ => ProviderFailureKind.CopyProblem
    };

    /// <summary>The provider's <c>retryAfterSeconds</c>, kept between five minutes and a day; fifteen minutes when it gave none.</summary>
    public static TimeSpan ThrottleDelay(int? retryAfterSeconds) =>
        retryAfterSeconds is > 0
            ? TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds.Value, MinimumThrottleDelay.TotalSeconds, MaximumThrottleDelay.TotalSeconds))
            : DefaultThrottleDelay;
}
