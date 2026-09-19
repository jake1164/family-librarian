using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// An administrator-registered out-of-process provider, reached over the
/// versioned external-provider HTTP protocol.
/// </summary>
/// <remarks>
/// Unlike <c>ProviderRegistry</c>'s hardcoded allowlist — deliberately closed,
/// so no configuration or request body can introduce a new provider — this is
/// a multi-row table by design: an external provider only exists because an
/// administrator explicitly registered one. <see cref="CachedEgressPolicy"/>
/// and the other manifest-derived <c>Cached*</c> fields (protocol version,
/// capabilities, instance id, egress policy) come only from the provider's
/// own <c>/manifest</c> response at registration/Test Connection time; they
/// are never admin-typed, since the provider is the one declaring what it
/// needs. <see cref="CachedHealthStatus"/>/<see cref="CachedSearchOperationStatus"/>/
/// <see cref="CachedAcquireOperationStatus"/> are the exception: per
/// docs/04-external-provider-http-protocol.md §5, <c>/health</c> is called
/// both at Test Connection and on the registration's own recheck schedule
/// (see <see cref="RecordHealthCheck"/>), so those three fields — and
/// <see cref="LastTestedAtUtc"/>/<see cref="LastTestSucceeded"/>/
/// <see cref="LastTestMessage"/> alongside them — reflect whichever probe ran
/// most recently, not only an admin's explicit click.
/// </remarks>
public sealed class ExternalProvider
{
    private ExternalProvider()
    {
    }

    public ExternalProvider(string providerId, string displayName, string baseUrl, DateTimeOffset createdAtUtc)
    {
        ProviderId = RequireText(providerId, nameof(providerId)).ToLowerInvariant();
        DisplayName = RequireText(displayName, nameof(displayName));
        BaseUrl = RequireText(baseUrl, nameof(baseUrl));
        IsEnabled = false;
        CachedEgressPolicy = EgressPolicy.Normal;
        RecheckSchedule = ProviderRecheckSchedule.Manual;
        AutoAcquireEnabled = false;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Admin-chosen, unique, stable slug — this is the <c>providerId</c> the rest of the app addresses it by.</summary>
    public string ProviderId { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    public string BaseUrl { get; private set; } = null!;

    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Administrator-controlled retry cadence for discovery only. On its own
    /// it never authorizes unattended acquisition from this external
    /// provider — a due recheck that finds a high-confidence candidate is
    /// still just recorded for review unless <see cref="AutoAcquireEnabled"/>
    /// is separately turned on for this provider.
    /// </summary>
    public ProviderRecheckSchedule RecheckSchedule { get; private set; }

    /// <summary>
    /// A separate, explicit admin toggle authorizing unattended acquisition
    /// of a high-confidence (<c>Identifier</c>-basis) candidate found on a
    /// scheduled recheck. Defaults to <c>false</c> for every newly registered
    /// provider, regardless of what its manifest claims to support or
    /// whether <see cref="RecheckSchedule"/> is set — never inferred from
    /// either, and freely settable at any time
    /// (family-librarian-provider-alpha5-plan.md §B). A
    /// <c>TitleAuthor</c>-basis candidate is never eligible for automatic
    /// acquisition regardless of this setting (§F2) — that gate lives in
    /// <c>ExternalProviderRecheckService</c>, not here.
    /// </summary>
    public bool AutoAcquireEnabled { get; private set; }

    public string? ProtectedApiKey { get; private set; }

    public int ApiKeyFormatVersion { get; private set; }

    public string? ApiKeyHint { get; private set; }

    public DateTimeOffset? ApiKeySetAtUtc { get; private set; }

    public string? CachedProtocolVersion { get; private set; }

    /// <summary>Comma-separated, as declared by the provider's own manifest.</summary>
    public string? CachedCapabilities { get; private set; }

    /// <summary>
    /// This deployed instance's own identity, as declared by its manifest
    /// (protocol v2 §4/§20) — distinct from <see cref="ProviderId"/>, which
    /// identifies the provider software, not this specific running copy of
    /// it. <c>null</c> when the provider never declares one (tolerated) or
    /// hasn't been successfully tested yet.
    /// </summary>
    public string? CachedInstanceId { get; private set; }

    /// <summary>
    /// True once a successful test observes an <see cref="CachedInstanceId"/>
    /// different from the previous successful test's — a container/instance
    /// swap mid-job is exactly the signal protocol v2 §20's provider-replacement
    /// detection needs. Reset to false whenever the instance id matches (or
    /// is newly recorded for the first time).
    /// </summary>
    public bool InstanceReplacedSincePreviousTest { get; private set; }

    public EgressPolicy CachedEgressPolicy { get; private set; }

    /// <summary>
    /// Coarse overall health from the provider's last successful test, per
    /// protocol v2 §5 — one of <c>Healthy</c>/<c>Degraded</c>/<c>Unhealthy</c>.
    /// A raw string, like <see cref="CachedCapabilities"/>: the wire
    /// vocabulary is Application/Infrastructure's concern to parse, not
    /// Domain's to model as its own enum.
    /// </summary>
    public string? CachedHealthStatus { get; private set; }

    /// <summary>One of <c>Available</c>/<c>Degraded</c>/<c>Unavailable</c>.</summary>
    public string? CachedSearchOperationStatus { get; private set; }

    /// <summary>One of <c>Available</c>/<c>Degraded</c>/<c>Unavailable</c>.</summary>
    public string? CachedAcquireOperationStatus { get; private set; }

    public string? CachedManagementUrl { get; private set; }

    public string? CachedDocumentationUrl { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    /// <summary>
    /// An administrator-chosen replacement for <see cref="CachedEgressPolicy"/>.
    /// <c>null</c> means "use the provider's own declared policy" (the default).
    /// Survives re-tests: <see cref="RecordTestResult"/> only ever updates
    /// <see cref="CachedEgressPolicy"/>, never this.
    /// </summary>
    public EgressPolicy? EgressPolicyOverride { get; private set; }

    /// <summary>What actually governs routing for this provider right now.</summary>
    public EgressPolicy EffectiveEgressPolicy => EgressPolicyOverride ?? CachedEgressPolicy;

    public bool HasApiKey => !string.IsNullOrEmpty(ProtectedApiKey);

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetRecheckSchedule(
        ProviderRecheckSchedule schedule,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        RecheckSchedule = schedule;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetEgressPolicyOverride(EgressPolicy? policy, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        EgressPolicyOverride = policy;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetAutoAcquireEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        AutoAcquireEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetDetails(string displayName, string baseUrl, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        DisplayName = RequireText(displayName, nameof(displayName));
        BaseUrl = RequireText(baseUrl, nameof(baseUrl));
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetApiKey(
        string protectedValue, int formatVersion, string? hint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedApiKey = protectedValue;
        ApiKeyFormatVersion = formatVersion;
        ApiKeyHint = hint;
        ApiKeySetAtUtc = updatedAtUtc;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearApiKey(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedApiKey = null;
        ApiKeyFormatVersion = 0;
        ApiKeyHint = null;
        ApiKeySetAtUtc = null;
        ResetTestResult();
        Touch(actorUserId, updatedAtUtc);
    }

    public void RecordTestResult(
        bool succeeded,
        string? message,
        string? protocolVersion,
        string? capabilities,
        EgressPolicy egressPolicy,
        Guid? actorUserId,
        DateTimeOffset testedAtUtc,
        string? instanceId = null,
        string? healthStatus = null,
        string? searchOperationStatus = null,
        string? acquireOperationStatus = null,
        string? managementUrl = null,
        string? documentationUrl = null,
        bool manifestReached = false)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = Truncate(message, 512);

        // Deliberately gated on manifestReached, not succeeded: succeeded
        // reflects whether /health came back fully operational, but a
        // degraded/unhealthy result is still a real, freshly observed
        // response that the Test Connection UI's health/search/acquire
        // chips must reflect -- otherwise they keep showing whatever the
        // previous (possibly healthy) test cached while the banner text
        // above them, built from the same probe, already reports the
        // degradation.
        if (manifestReached)
        {
            CachedProtocolVersion = protocolVersion;
            CachedCapabilities = capabilities;
            CachedEgressPolicy = egressPolicy;
            CachedHealthStatus = healthStatus;
            CachedSearchOperationStatus = searchOperationStatus;
            CachedAcquireOperationStatus = acquireOperationStatus;
            CachedManagementUrl = managementUrl;
            CachedDocumentationUrl = documentationUrl;

            // A previously-observed instance id that changes to a different
            // (non-empty) one is exactly the container-replacement signal
            // protocol v2 §20 asks for. A provider that has never declared
            // one, or declares the same one again, is not a replacement.
            InstanceReplacedSincePreviousTest =
                !string.IsNullOrWhiteSpace(CachedInstanceId) &&
                !string.IsNullOrWhiteSpace(instanceId) &&
                !string.Equals(CachedInstanceId, instanceId, StringComparison.Ordinal);
            CachedInstanceId = instanceId ?? CachedInstanceId;
        }

        Touch(actorUserId, testedAtUtc);
    }

    /// <summary>
    /// Records a live <c>/health</c> probe made outside an admin's "Test
    /// Connection" click — currently <c>ExternalProviderRecheckService</c>'s
    /// own probe on this provider's <see cref="RecheckSchedule"/> cadence
    /// (docs/04 §5). Deliberately narrower than <see cref="RecordTestResult"/>:
    /// a recheck never calls <c>/manifest</c>, so this must not touch protocol
    /// version, capabilities, instance id, or egress policy — those stay
    /// exactly what the last real Test Connection observed. Also does not
    /// call <see cref="Touch"/>: <see cref="UpdatedByUserId"/>/
    /// <see cref="UpdatedAtUtc"/> record an administrator's own edits, and an
    /// unattended background probe is not one.
    /// </summary>
    public void RecordHealthCheck(
        bool succeeded,
        string? message,
        string? healthStatus,
        string? searchOperationStatus,
        string? acquireOperationStatus,
        DateTimeOffset checkedAtUtc)
    {
        LastTestedAtUtc = checkedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = Truncate(message, 512);
        CachedHealthStatus = healthStatus;
        CachedSearchOperationStatus = searchOperationStatus;
        CachedAcquireOperationStatus = acquireOperationStatus;
    }

    private void ResetTestResult()
    {
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
