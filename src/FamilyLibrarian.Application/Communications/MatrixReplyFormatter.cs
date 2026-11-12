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

    public static string ToText(ConversationReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var cleaned = new string(reply.Text.Where(character => !char.IsControl(character) || character == '\n').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return "Say “help” to see what I can do.";
        }

        if (cleaned.Length <= MaxLength)
        {
            return cleaned;
        }

        // Cut at a line break so a numbered list is never split mid-entry.
        var cut = cleaned.LastIndexOf('\n', MaxLength - 1);
        return (cut > 0 ? cleaned[..cut] : cleaned[..(MaxLength - 1)]).TrimEnd() + "\n…";
    }
}
