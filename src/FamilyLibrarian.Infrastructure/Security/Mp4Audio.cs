namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// The audiobook extensions that are an ISO base media (MP4) container: .m4b and .m4a differ only in name, so both are
/// validated, inspected and identity-checked by the same MP4 readers.
/// </summary>
internal static class Mp4Audio
{
    public static bool IsContainerFormat(string? format) =>
        string.Equals(format, ".m4b", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, ".m4a", StringComparison.OrdinalIgnoreCase);
}
