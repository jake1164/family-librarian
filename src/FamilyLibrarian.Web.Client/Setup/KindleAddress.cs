namespace FamilyLibrarian.Web.Client.Setup;

public static class KindleAddress
{
    /// <summary>
    /// A hint, never a refusal: Amazon's address domains vary, but an ordinary email address typed here is the
    /// most likely mistake.
    /// </summary>
    public static string? Warning(string? address)
    {
        var value = address?.Trim();
        var at = value?.LastIndexOf('@') ?? -1;
        if (value is null || at < 1 || !value[(at + 1)..].Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        return value[(at + 1)..].Contains("kindle", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Kindle addresses usually end in @kindle.com. Check that this isn't your own email address.";
    }
}
