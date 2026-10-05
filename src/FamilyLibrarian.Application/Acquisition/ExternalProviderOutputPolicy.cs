using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Size and count limits for one media type's external-provider output.</summary>
public sealed class ExternalProviderOutputLimits
{
    public int MaxOutputCount { get; set; }
    public long MaxFileBytes { get; set; }
    public long MaxJobBytes { get; set; }

    public bool IsValid => MaxOutputCount > 0 && MaxFileBytes > 0 && MaxJobBytes >= MaxFileBytes;
}

/// <summary>Independent resource limits for untrusted external-provider output streams.</summary>
/// <remarks>
/// Ebooks and audiobooks have very different realistic sizes (a 30-hour, 128 kbps
/// audiobook is ~1.7 GB as a single file), so size and count limits are set per
/// media type. Always read them through <see cref="ForMediaType"/>; the flat
/// <see cref="MaxOutputCount"/>, <see cref="MaxFileBytes"/> and <see cref="MaxJobBytes"/>
/// values are only the fallback when a media type has no limits of its own.
/// </remarks>
public sealed class ExternalProviderOutputPolicy
{
    public const string SectionName = "ExternalProviderOutput";
    public const int DefaultMaxOutputCount = 64;
    public const long DefaultMaxFileBytes = 1L * 1024 * 1024 * 1024;
    public const long DefaultMaxJobBytes = 4L * 1024 * 1024 * 1024;
    public const int DefaultMaxFilenameLength = 1_024;
    public const int DefaultReadInactivityTimeoutSeconds = 120;
    public const long DefaultMinFreeDiskBytes = 1L * 1024 * 1024 * 1024;

    public int MaxOutputCount { get; set; } = DefaultMaxOutputCount;
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;
    public long MaxJobBytes { get; set; } = DefaultMaxJobBytes;
    public int MaxFilenameLength { get; set; } = DefaultMaxFilenameLength;
    public int ReadInactivityTimeoutSeconds { get; set; } = DefaultReadInactivityTimeoutSeconds;

    /// <summary>Free space that must remain on the staging volume after a download. 0 disables the check.</summary>
    public long MinFreeDiskBytes { get; set; } = DefaultMinFreeDiskBytes;

    public ExternalProviderOutputLimits? Ebook { get; set; } = new()
    {
        MaxOutputCount = 16,
        MaxFileBytes = 256L * 1024 * 1024,
        MaxJobBytes = 512L * 1024 * 1024
    };

    public ExternalProviderOutputLimits? Audiobook { get; set; } = new()
    {
        MaxOutputCount = 256,
        MaxFileBytes = 4L * 1024 * 1024 * 1024,
        MaxJobBytes = 8L * 1024 * 1024 * 1024
    };

    public bool IsValid => MaxOutputCount > 0 && MaxFileBytes > 0 && MaxJobBytes >= MaxFileBytes &&
        MaxFilenameLength > 0 && ReadInactivityTimeoutSeconds > 0 && MinFreeDiskBytes >= 0 &&
        (Ebook?.IsValid ?? true) && (Audiobook?.IsValid ?? true);

    /// <summary>Returns a policy whose flat size and count limits are those of <paramref name="mediaType"/>.</summary>
    public ExternalProviderOutputPolicy ForMediaType(RequestMediaType mediaType)
    {
        var limits = mediaType == RequestMediaType.Ebook ? Ebook : Audiobook;
        if (limits is null)
            return this;
        return new ExternalProviderOutputPolicy
        {
            MaxOutputCount = limits.MaxOutputCount,
            MaxFileBytes = limits.MaxFileBytes,
            MaxJobBytes = limits.MaxJobBytes,
            MaxFilenameLength = MaxFilenameLength,
            ReadInactivityTimeoutSeconds = ReadInactivityTimeoutSeconds,
            MinFreeDiskBytes = MinFreeDiskBytes,
            Ebook = Ebook,
            Audiobook = Audiobook
        };
    }
}
