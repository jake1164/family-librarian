namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// Cheap, deterministic checks that decide whether a search message is worth sending to the optional interpreter even though the
/// catalog returned something. No model is involved here: this only avoids a model call for plain titles.
/// </summary>
internal static class ConversationProse
{
    private const int LongMessageWords = 7;
    private const int MinimumWords = 3;

    private static readonly HashSet<string> LeadingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "whats", "what's", "which", "who", "whos", "who's", "where", "when", "how", "why",
        "is", "are", "was", "do", "does", "did", "can", "could", "would", "should",
        "i", "i'm", "im", "i've", "ive", "my", "we", "please", "no", "not", "actually", "sorry",
        "looking", "need", "want", "wanted", "recommend", "suggest", "tell", "show", "give", "get"
    };

    /// <summary>
    /// A question mark, a conversational opening word ("what", "I", "no, ...") or a long message reads as a sentence. A short
    /// message like "dune frank herbert" is treated as a title and searched as typed.
    /// </summary>
    public static bool LooksLikeProse(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < MinimumWords)
        {
            return false;
        }

        if (text.Contains('?', StringComparison.Ordinal) || words.Length >= LongMessageWords)
        {
            return true;
        }

        return LeadingWords.Contains(words[0].Trim(',', '.', '!', ';', ':'));
    }

    /// <summary>True when one result's title is, ignoring case and punctuation, the whole message: the person typed a title.</summary>
    public static bool HasExactTitle(IReadOnlyList<ConversationChoiceRef> choices, string text)
    {
        var wanted = Fold(text);
        return wanted.Length > 0 && choices.Any(choice => Fold(choice.Title) == wanted);
    }

    private static string Fold(string value) =>
        string.Join(' ', new string([.. value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
