namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The content type a genuine file with each supported extension must sniff as.
/// </summary>
/// <remarks>
/// Used to reject a file whose real bytes do not match its claimed extension —
/// a renamed executable or a corrupt download — independent of malware
/// scanning, which arrives in M10.
/// </remarks>
public static class KnownFormatContentTypes
{
    public static readonly IReadOnlyDictionary<string, string> ByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".epub"] = "application/epub+zip",
            [".pdf"] = "application/pdf",
            [".mp3"] = "audio/mpeg",
            [".m4b"] = "audio/mp4"
        };
}
