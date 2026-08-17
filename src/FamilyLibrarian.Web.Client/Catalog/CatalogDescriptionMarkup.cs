using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace FamilyLibrarian.Web.Client.Catalog;

/// <summary>
/// Converts an untrusted metadata-provider description into the small, safe markup subset that the
/// catalog detail pages render. Attributes are intentionally unsupported so provider content cannot
/// introduce links, remote assets, styles, or event handlers.
/// </summary>
public static partial class CatalogDescriptionMarkup
{
    [GeneratedRegex(
        "&lt;/?(?:p|br|strong|b|em|i|ul|ol|li|blockquote)\\s*/?&gt;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedTags();

    /// <summary>
    /// HTML-encodes all provider content, then restores only attribute-free structural and emphasis tags.
    /// The result is safe to render as a <see cref="MarkupString"/>.
    /// </summary>
    public static MarkupString ToMarkup(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var encodedDescription = WebUtility.HtmlEncode(WebUtility.HtmlDecode(description));
        var openTags = new Stack<string>();
        var safeHtml = AllowedTags().Replace(encodedDescription, match =>
        {
            var tag = match.Value[4..^4];
            if (tag[0] == '/')
            {
                var closingTagName = tag[1..].Trim();
                if (!openTags.TryPeek(out var openTagName) ||
                    !string.Equals(openTagName, closingTagName, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                openTags.Pop();
                return $"<{tag}>";
            }

            var isSelfClosing = tag.EndsWith('/');
            var openingTagName = tag.Trim().TrimEnd('/').Trim();
            if (!isSelfClosing && !string.Equals(openingTagName, "br", StringComparison.OrdinalIgnoreCase))
            {
                openTags.Push(openingTagName);
            }

            return $"<{tag}>";
        });

        return new MarkupString(safeHtml);
    }
}
