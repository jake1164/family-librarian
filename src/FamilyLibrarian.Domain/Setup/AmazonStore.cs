using System.Text.RegularExpressions;

namespace FamilyLibrarian.Domain.Setup;

/// <summary>
/// Turns whatever an administrator typed or pasted into an Amazon store domain, and builds the account links from it.
/// There is deliberately no list of stores to maintain: any <c>amazon.&lt;country&gt;</c> domain is accepted. That
/// checks the shape of the address only. It cannot tell whether Amazon runs that domain, which is why the
/// administrator is given the built links to open and check.
/// </summary>
public static partial class AmazonStore
{
    public const string DefaultDomain = "amazon.com";

    /// <param name="domain"><see langword="null"/> when nothing was entered, meaning <see cref="DefaultDomain"/>.</param>
    /// <returns><see langword="false"/> when the input is not an Amazon address.</returns>
    public static bool TryNormalize(string? input, out string? domain)
    {
        domain = null;
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"https://{value}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0)
        {
            return false;
        }

        var match = StorePattern().Match(uri.IdnHost.TrimEnd('.').ToLowerInvariant());
        if (!match.Success)
        {
            return false;
        }

        domain = match.Groups["store"].Value;
        return true;
    }

    public static string FindAddressUrl(string? domain) => $"https://www.{domain ?? DefaultDomain}/sendtokindle/email";

    public static string ManageContentUrl(string? domain) => $"https://www.{domain ?? DefaultDomain}/hz/mycd/myx";

    // The store is the end of the host name, so subdomains are dropped and "amazon.example.net" is refused.
    [GeneratedRegex(@"(^|\.)(?<store>amazon\.(com|[a-z]{2}|co\.[a-z]{2}|com\.[a-z]{2}))\z", RegexOptions.CultureInvariant)]
    private static partial Regex StorePattern();
}
