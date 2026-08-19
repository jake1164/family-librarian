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
/// and the other <c>Cached*</c> fields come only from the provider's own
/// <c>/manifest</c> response at registration/Test Connection time; they are
/// never admin-typed, since the provider is the one declaring what it needs.
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
    /// Administrator-controlled retry cadence for discovery only. It never
    /// authorizes unattended acquisition from this external provider.
    /// </summary>
    public ProviderRecheckSchedule RecheckSchedule { get; private set; }

    public string? ProtectedApiKey { get; private set; }

    public int ApiKeyFormatVersion { get; private set; }

    public string? ApiKeyHint { get; private set; }

    public DateTimeOffset? ApiKeySetAtUtc { get; private set; }

    public string? CachedProtocolVersion { get; private set; }

    /// <summary>Comma-separated, as declared by the provider's own manifest.</summary>
    public string? CachedCapabilities { get; private set; }

    public EgressPolicy CachedEgressPolicy { get; private set; }

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
        DateTimeOffset testedAtUtc)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = Truncate(message, 512);

        if (succeeded)
        {
            CachedProtocolVersion = protocolVersion;
            CachedCapabilities = capabilities;
            CachedEgressPolicy = egressPolicy;
        }

        Touch(actorUserId, testedAtUtc);
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
