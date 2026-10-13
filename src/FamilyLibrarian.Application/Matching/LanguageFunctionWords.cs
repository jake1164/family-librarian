namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// Infers the language of a short descriptive text from closed lists of very common function words. It exists for
/// audiobook tags that describe a book in the book's language without declaring it (a German blurb and genre on a file
/// that has no language tag). Deliberately conservative: it decides only when the evidence is plentiful and one
/// language clearly dominates, and otherwise says nothing. Words that more than one listed language uses heavily
/// ("de", "la", "que", "se", "en", "was", "die") are left out of every list.
/// </summary>
public static class LanguageFunctionWords
{
    /// <summary>The fewest matched function words that can decide a language.</summary>
    public const int MinimumEvidence = 12;

    /// <summary>The top language must have at least this many times the runner-up's matches.</summary>
    public const int MinimumMargin = 3;

    /// <summary>Text beyond this many characters is not read.</summary>
    public const int MaxTextLength = 4096;

    private static readonly (string Language, HashSet<string> Words)[] Lists =
    [
        ("en", Words("the and of to is that it for with as his her she they but not you are this have from by at be had which were their who what will been has one all when there he")),
        ("de", Words("der das und ist nicht ein eine mit von zu den dem des auf für sich auch sie er wird werden bei aus nach wie oder aber dass wenn noch nur über einen im am als hat sind ihr ihre")),
        ("fr", Words("le les des du une est qui pour dans avec sur pas au aux ce cette il elle ils elles nous vous mais ou où sont être avoir plus sa ses leur leurs ont été fait comme tout aussi entre sans")),
        ("es", Words("el los las una es por para con del al su sus como pero más este esta esto muy también sin sobre cuando hay fue ser está han ha lo ya")),
    ];

    /// <summary>
    /// True when <paramref name="text"/> is clearly written in one listed language. <paramref name="evidenceCount"/> is
    /// that language's number of matched function words; it is only meaningful when the method returns true.
    /// </summary>
    public static bool TryInfer(string? text, out string language, out int evidenceCount)
    {
        (language, evidenceCount) = (string.Empty, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var counts = new int[Lists.Length];
        var bounded = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        var start = -1;
        for (var index = 0; index <= bounded.Length; index++)
        {
            var isLetter = index < bounded.Length && char.IsLetter(bounded[index]);
            if (isLetter)
            {
                if (start < 0) start = index;
                continue;
            }

            if (start >= 0)
            {
                var word = bounded.AsSpan(start, index - start).ToString().ToLowerInvariant();
                for (var list = 0; list < Lists.Length; list++)
                {
                    if (Lists[list].Words.Contains(word)) counts[list]++;
                }

                start = -1;
            }
        }

        var top = Array.IndexOf(counts, counts.Max());
        var runnerUp = counts.Where((_, position) => position != top).Max();
        if (counts[top] < MinimumEvidence || counts[top] < MinimumMargin * runnerUp)
        {
            return false;
        }

        (language, evidenceCount) = (Lists[top].Language, counts[top]);
        return true;
    }

    private static HashSet<string> Words(string list) =>
        new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
