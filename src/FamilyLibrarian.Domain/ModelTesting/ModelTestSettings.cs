namespace FamilyLibrarian.Domain.ModelTesting;

/// <summary>
/// Administrator-managed configuration for the optional model connectivity and capability test (alpha.6 M3-5). One row,
/// created on first configuration. Disabled and empty by default: nothing here names a real endpoint, credential or model.
/// The API key is stored protected and is write-only outside this aggregate.
/// </summary>
/// <remarks>
/// This only configures an administrator's manual test harness. No part of Family Librarian reads these settings to decide
/// anything about matching, requests, approvals or publishing.
/// </remarks>
public sealed class ModelTestSettings
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MinimumTimeoutSeconds = 5;
    public const int MaximumTimeoutSeconds = 120;
    public const int DefaultMaxOutputTokens = 384;
    public const int MinimumMaxOutputTokens = 16;
    public const int MaximumMaxOutputTokens = 1_024;
    public const int MaxModelNameLength = 128;
    public const int MaxEndpointLength = 512;

    private ModelTestSettings()
    {
    }

    public ModelTestSettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    public string? EndpointUrl { get; private set; }

    public string? ProtectedApiKey { get; private set; }

    public int ApiKeyFormatVersion { get; private set; }

    public DateTimeOffset? ApiKeySetAtUtc { get; private set; }

    /// <summary>The primary local model alias to test.</summary>
    public string? PrimaryModel { get; private set; }

    /// <summary>An optional second local alias for side-by-side comparison.</summary>
    public string? ComparisonModel { get; private set; }

    /// <summary>An optional cloud-backed alias. Used only when <see cref="AllowCloud"/> is on and the administrator selects it for a run.</summary>
    public string? CloudModel { get; private set; }

    public bool AllowCloud { get; private set; }

    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public int MaxOutputTokens { get; private set; } = DefaultMaxOutputTokens;

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ProtectedApiKey);

    public void Configure(
        string? endpointUrl, string? primaryModel, string? comparisonModel, string? cloudModel, bool allowCloud,
        int timeoutSeconds, int maxOutputTokens, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (timeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        if (maxOutputTokens is < MinimumMaxOutputTokens or > MaximumMaxOutputTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        EndpointUrl = Clean(endpointUrl, MaxEndpointLength)?.TrimEnd('/');
        PrimaryModel = Clean(primaryModel, MaxModelNameLength);
        ComparisonModel = Clean(comparisonModel, MaxModelNameLength);
        CloudModel = Clean(cloudModel, MaxModelNameLength);
        AllowCloud = allowCloud;
        TimeoutSeconds = timeoutSeconds;
        MaxOutputTokens = maxOutputTokens;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetApiKey(string protectedValue, int formatVersion, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("A protected value is required.", nameof(protectedValue));
        }

        ProtectedApiKey = protectedValue;
        ApiKeyFormatVersion = formatVersion;
        ApiKeySetAtUtc = updatedAtUtc;
        Touch(actorUserId, updatedAtUtc);
    }

    public void ClearApiKey(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ProtectedApiKey = null;
        ApiKeyFormatVersion = 0;
        ApiKeySetAtUtc = null;
        Touch(actorUserId, updatedAtUtc);
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
