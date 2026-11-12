namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// A small closed list of words that state a release's language without naming
/// it ("ungekürzt" is German audiobook packaging). Like
/// <see cref="LanguageAcceptance.TryResolveLanguageName"/> it reads uppercase
/// release tokens, and it is only a fallback for a source that gave no
/// structured language and no language name. Not a language detector: a word
/// outside this list says nothing.
/// </summary>
public static class LanguageCueWords
{
    private static readonly (string Language, string[] Phrase)[] Cues =
    [
        ("de", ["UNGEKÜRZT"]), ("de", ["UNGEKUERZT"]), ("de", ["GEKÜRZT"]), ("de", ["GEKUERZT"]),
        ("de", ["HÖRBUCH"]), ("de", ["HOERBUCH"]), ("de", ["HÖRSPIEL"]), ("de", ["HOERSPIEL"]),
        ("fr", ["LIVRE", "AUDIO"]), ("fr", ["LU", "PAR"]), ("fr", ["INTÉGRALE"]),
    ];

    /// <summary>The first cue found in <paramref name="tokens"/> that is not already <paramref name="consumed"/>.</summary>
    public static bool TryFind(IReadOnlyList<string> tokens, IReadOnlyList<bool> consumed,
        out string language, out int start, out int length)
    {
        foreach (var (cueLanguage, phrase) in Cues)
        {
            for (var index = 0; index + phrase.Length <= tokens.Count; index++)
            {
                if (Enumerable.Range(index, phrase.Length).Any(position => consumed[position]) ||
                    !phrase.Select((word, offset) => tokens[index + offset] == word).All(same => same))
                {
                    continue;
                }

                (language, start, length) = (cueLanguage, index, phrase.Length);
                return true;
            }
        }

        (language, start, length) = (string.Empty, -1, 0);
        return false;
    }
}
