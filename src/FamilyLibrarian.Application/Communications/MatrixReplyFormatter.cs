using System.Globalization;
using FamilyLibrarian.Application.Conversation;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Turns a conversation reply into the message Matrix shows. Plain text only, so every client renders it the same way and
/// nothing a catalog source supplied can be interpreted as markup: control characters are removed and the length is bounded.
/// Numbered choices already read as a list in the text itself.
/// </summary>
public static class MatrixReplyFormatter
{
    public const int MaxLength = 1_800;

    private const string LinkLabel = "Open in Family Librarian: ";

    public static string ToText(ConversationReply reply) => ToText(reply, null);

    /// <summary>
    /// The Matrix text, plus at most one link to the saved catalog record the answer is about when the deployment has a trusted
    /// public origin (<c>Interaction:PublicOrigin</c>, validated at startup). The link is built only from an application route
    /// with a saved id that the reply's answer card carries, never from a provider string. The link is appended only when the
    /// full bounded answer and the link together stay within <see cref="MaxLength"/>; otherwise the answer is sent without it. Without an origin, or without a saved record, the text is unchanged.
    /// </summary>
    public static string ToText(ConversationReply reply, string? publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var cleaned = new string(reply.Text.Where(character => !char.IsControl(character) || character == '\n').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return "Say “help” to see what I can do.";
        }

        // The answer is bounded exactly as it is without a link; the link is optional and is added only when the whole bounded
        // answer, qualifications and follow-up instructions included, and the link still fit. It never costs the answer a line.
        var answer = Bound(cleaned, MaxLength);
        var link = CatalogLink(reply, publicOrigin);
        if (link is null)
        {
            return answer;
        }

        var linkLine = LinkLabel + link;
        return answer.Length + 1 + linkLine.Length <= MaxLength ? answer + "\n" + linkLine : answer;
    }

    /// <summary>
    /// The absolute catalog address for the record the answer is about, or null. The answer's own subject (a saved series) is
    /// preferred; otherwise the first saved record it lists. Only <c>/works/{guid}</c> and <c>/series/{guid}</c> qualify.
    /// </summary>
    public static string? CatalogLink(ConversationReply reply, string? publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!TryOrigin(publicOrigin, out var origin) || reply.Answers is not { Count: > 0 } answers)
        {
            return null;
        }

        var route = answers.Select(card => card.Href).FirstOrDefault(IsCatalogRoute)
            ?? answers.SelectMany(card => card.Lines).Select(line => line.Href).FirstOrDefault(IsCatalogRoute);
        return route is null ? null : new Uri(origin, route).AbsoluteUri;
    }

    private static bool TryOrigin(string? value, out Uri origin)
    {
        origin = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 ||
            parsed.UserInfo.Length > 0)
        {
            return false;
        }

        origin = parsed;
        return true;
    }

    private static bool IsCatalogRoute(string? href)
    {
        if (href is null)
        {
            return false;
        }

        foreach (var prefix in (ReadOnlySpan<string>)["/works/", "/series/"])
        {
            if (href.StartsWith(prefix, StringComparison.Ordinal))
            {
                var id = href.AsSpan(prefix.Length);
                return Guid.TryParseExact(id, "D", out var guid) && guid != Guid.Empty &&
                       string.Equals(guid.ToString("D", CultureInfo.InvariantCulture), id.ToString(), StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string Bound(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        // Cut at a line break so a numbered list is never split mid-entry, leaving room for the two-character "\n…" marker.
        var room = limit - 2;
        var cut = text.LastIndexOf('\n', room - 1);
        return (cut > 0 ? text[..cut] : text[..room]).TrimEnd() + "\n…";
    }
}
