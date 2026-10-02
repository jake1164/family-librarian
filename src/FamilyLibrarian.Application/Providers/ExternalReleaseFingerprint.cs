using System.Security.Cryptography;
using System.Text;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Recognizes the same release posted more than once under different provider
/// references, so a copy that already failed is not downloaded again under a
/// different ID (PROVIDER-7).
/// </summary>
/// <remarks>
/// Deliberately conservative: it needs <em>both</em> a release name and a size
/// to say anything, and it compares only normalized letters and digits, so
/// <c>Ray.Bradbury-Fahrenheit.451</c> and <c>Ray Bradbury - Fahrenheit 451</c>
/// of the same byte size are one release while a differently sized one is not.
/// With either fact missing there is no fingerprint, and nothing is ever
/// called equivalent on thin evidence. The result is a hash, not the name,
/// because it is stored and compared, never displayed.
/// </remarks>
public static class ExternalReleaseFingerprint
{
    public static string? Compute(string? releaseName, long? sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(releaseName) || sizeBytes is not > 0)
        {
            return null;
        }

        var normalized = new StringBuilder(releaseName.Length);
        foreach (var character in releaseName.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToUpperInvariant(character));
            }
        }

        if (normalized.Length == 0)
        {
            return null;
        }

        var payload = Encoding.UTF8.GetBytes($"{normalized}|{sizeBytes.Value}");
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
