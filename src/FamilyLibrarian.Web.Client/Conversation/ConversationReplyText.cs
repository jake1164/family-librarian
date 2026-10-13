using System.Text.RegularExpressions;
using FamilyLibrarian.Contracts.Conversation;

namespace FamilyLibrarian.Web.Client.Conversation;

/// <summary>Trims a reply's plain text for the web page, which also renders the results and requests as lists.</summary>
public static partial class ConversationReplyText
{
    private const char Bullet = '•';

    /// <summary>
    /// Removes the numbered result lines when the reply carries structured choices, and the bullet lines when it carries
    /// structured requests. Everything else is kept as written. A reply with neither is returned unchanged.
    /// </summary>
    public static string ForStructuredDisplay(ConversationReplyResponse reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var dropChoices = reply.Choices.Count > 0;
        var dropRequests = reply.Requests.Count > 0;
        if (!dropChoices && !dropRequests)
        {
            return reply.Text;
        }

        var kept = reply.Text
            .Split('\n')
            .Where(line => !(dropChoices && NumberedLine().IsMatch(line)) && !(dropRequests && line.TrimStart().StartsWith(Bullet)))
            .ToArray();
        return string.Join('\n', kept).Trim();
    }

    [GeneratedRegex(@"^\s*\d{1,2}\.\s")]
    private static partial Regex NumberedLine();
}
