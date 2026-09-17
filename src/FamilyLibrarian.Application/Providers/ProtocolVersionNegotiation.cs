namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Protocol v2 §4/§21 version negotiation: version strings are positive
/// base-10 integer major versions, compared numerically (never
/// lexicographically — <c>"10"</c> is higher than <c>"2"</c>).
/// </summary>
public static class ProtocolVersionNegotiation
{
    /// <summary>The protocol versions Family Librarian itself can speak, highest first.</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = ["2", "1"];

    /// <summary>
    /// Returns the highest version present in both <paramref name="providerVersions"/>
    /// and <see cref="SupportedVersions"/>, or <c>null</c> if there is no
    /// overlap — callers must refuse <c>/search</c>/<c>/acquire</c> rather
    /// than guess when this returns <c>null</c>.
    /// </summary>
    public static string? Negotiate(IReadOnlyList<string> providerVersions)
    {
        var providerNumbers = providerVersions
            .Select(version => (Text: version, Parsed: int.TryParse(version, out var number) ? number : (int?)null))
            .Where(entry => entry.Parsed is not null)
            .ToDictionary(entry => entry.Parsed!.Value, entry => entry.Text);

        foreach (var supported in SupportedVersions)
        {
            if (int.TryParse(supported, out var supportedNumber) && providerNumbers.ContainsKey(supportedNumber))
            {
                return supported;
            }
        }

        return null;
    }
}
