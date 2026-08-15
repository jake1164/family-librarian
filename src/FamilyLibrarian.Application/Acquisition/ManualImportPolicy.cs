using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Deployment-tunable manual-import rules.
/// </summary>
/// <remarks>
/// A plain settings object rather than <c>IOptions</c>, mirroring
/// <see cref="Accounts.InvitationPolicy"/>: the application layer takes no
/// dependency on the options packages, so the host binds configuration and
/// supplies an instance.
/// </remarks>
public sealed class ManualImportPolicy
{
    public const string SectionName = "ManualImport";

    public const long DefaultMaxUploadSizeBytes = 500L * 1024 * 1024;

    public long MaxUploadSizeBytes { get; set; } = DefaultMaxUploadSizeBytes;

    public IList<string> AllowedEbookExtensions { get; set; } = [".epub", ".pdf"];

    public IList<string> AllowedAudiobookExtensions { get; set; } = [".m4b", ".mp3"];

    public bool IsValid =>
        MaxUploadSizeBytes > 0 &&
        AllowedEbookExtensions.Count > 0 &&
        AllowedAudiobookExtensions.Count > 0;

    public bool IsExtensionAllowed(RequestMediaType mediaType, string extension) =>
        AllowedExtensionsFor(mediaType).Contains(extension, StringComparer.OrdinalIgnoreCase);

    private IList<string> AllowedExtensionsFor(RequestMediaType mediaType) =>
        mediaType == RequestMediaType.Ebook ? AllowedEbookExtensions : AllowedAudiobookExtensions;
}
