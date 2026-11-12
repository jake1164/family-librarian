namespace FamilyLibrarian.Application.Matching;

/// <summary>A bounded spelling difference, usable only with exact title and independent artifact corroboration.</summary>
public static class AuthorSpelling
{
    public static bool IsVariant(string? expected, string? observed)
    {
        static string[] Components(string? name) => string.IsNullOrWhiteSpace(name) ? [] :
            DeterministicBookMatcher.NormalizeWords(AuthorNameForm.Canonical(name)).ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => !AuthorNameForm.Suffixes.Contains(c)).ToArray();
        var wanted = Components(expected);
        var actual = Components(observed);
        return wanted.Length > 0 && wanted.Length == actual.Length && wanted.Zip(actual).All(pair =>
            pair.First.Length <= 64 && pair.Second.Length <= 64 && pair.First[0] == pair.Second[0] &&
            AuthorAffinity.EditDistance(pair.First, pair.Second) <= 2);
    }
}
