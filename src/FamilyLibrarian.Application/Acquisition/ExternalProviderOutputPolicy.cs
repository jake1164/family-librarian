namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Independent resource limits for untrusted external-provider output streams.</summary>
public sealed class ExternalProviderOutputPolicy
{
    public const string SectionName = "ExternalProviderOutput";
    public const int DefaultMaxOutputCount = 64;
    public const long DefaultMaxFileBytes = 1L * 1024 * 1024 * 1024;
    public const long DefaultMaxJobBytes = 4L * 1024 * 1024 * 1024;
    public const int DefaultMaxFilenameLength = 1_024;
    public const int DefaultReadInactivityTimeoutSeconds = 120;

    public int MaxOutputCount { get; set; } = DefaultMaxOutputCount;
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;
    public long MaxJobBytes { get; set; } = DefaultMaxJobBytes;
    public int MaxFilenameLength { get; set; } = DefaultMaxFilenameLength;
    public int ReadInactivityTimeoutSeconds { get; set; } = DefaultReadInactivityTimeoutSeconds;

    public bool IsValid => MaxOutputCount > 0 && MaxFileBytes > 0 && MaxJobBytes >= MaxFileBytes &&
        MaxFilenameLength > 0 && ReadInactivityTimeoutSeconds > 0;
}
