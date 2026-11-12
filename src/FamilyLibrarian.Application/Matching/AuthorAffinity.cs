namespace FamilyLibrarian.Application.Matching;

public enum AuthorAffinityKind { Conflict, Unknown, Fuzzy, FirstOnly, FirstExactLastFuzzy, LastOnly, LastExactFirstFuzzy, Compatible, Exact, ContextCorroborated }

/// <summary>Supporting identity evidence, never a provider popularity or acceptance score.</summary>
public sealed record AuthorAffinityResult(
    AuthorAffinityKind Kind, int Score, string? WantedAuthor, string? DetectedAuthor,
    string FirstNameMatch, string LastNameMatch)
{
    public bool HasStrongSupport => Kind is AuthorAffinityKind.Exact or AuthorAffinityKind.Compatible
        or AuthorAffinityKind.LastExactFirstFuzzy or AuthorAffinityKind.FirstExactLastFuzzy;
}

public static class AuthorAffinity
{
    private static readonly HashSet<string> Suffixes = AuthorNameForm.Suffixes;

    public static AuthorAffinityResult Evaluate(string? wanted, string? detected, bool structured = true)
    {
        var expected = Tokens(wanted);
        var observed = Tokens(detected);
        AuthorAffinityResult Result(AuthorAffinityKind kind, int score, string first = "unknown", string last = "unknown") =>
            new(kind, score, wanted, detected, first, last);
        if (expected.Length == 0 || observed.Length == 0)
            return Result(AuthorAffinityKind.Unknown, 0);
        if (expected.ToHashSet(StringComparer.Ordinal).SetEquals(observed))
            return Result(AuthorAffinityKind.Exact, 100, "exact", "exact");
        // A single-component requested name ("Homer") can only be supported by that exact component.
        if (expected.Length < 2)
        {
            if (observed.Contains(expected[0], StringComparer.Ordinal))
                return Result(AuthorAffinityKind.LastOnly, 70, "unknown", "exact");
            return Result(structured ? AuthorAffinityKind.Conflict : AuthorAffinityKind.Unknown, structured ? -100 : 0);
        }
        var first = Best(expected[0], observed, allowInitial: true);
        var last = Best(expected[^1], observed, allowInitial: false);
        // An incompatible full component is contradictory, rather than a partial match.
        var extras = observed.Where(token => Match(expected[0], token, true) == "unknown" &&
            Match(expected[^1], token, false) == "unknown" && token.Length > 1 &&
            !expected.Contains(token, StringComparer.Ordinal)).ToArray();
        if (first != "unknown" && last != "unknown")
        {
            if (structured && extras.Length > 0)
                return Result(structured ? AuthorAffinityKind.Conflict : AuthorAffinityKind.Unknown, structured ? -100 : 0, first, last);
            if (first is "exact" or "initial" && last == "exact")
                return Result(AuthorAffinityKind.Compatible, 90, first, last);
            if (last == "exact" && first == "fuzzy")
                return Result(AuthorAffinityKind.LastExactFirstFuzzy, 80, first, last);
            if (first == "exact" && last == "fuzzy")
                return Result(AuthorAffinityKind.FirstExactLastFuzzy, 60, first, last);
            return Result(AuthorAffinityKind.Fuzzy, 40, first, last);
        }
        if (observed.Length == 1 && last == "exact") return Result(AuthorAffinityKind.LastOnly, 70, first, last);
        if (observed.Length == 1 && first == "exact") return Result(AuthorAffinityKind.FirstOnly, 50, first, last);
        // For raw residual release text, only a two-word name is clear negative evidence.
        var conflict = structured || observed.Length == 2 && observed.All(token => token.Length > 1 && token.All(char.IsLetter));
        return Result(conflict ? AuthorAffinityKind.Conflict : AuthorAffinityKind.Unknown, conflict ? -100 : 0, first, last);
    }

    internal static bool IsSupportingToken(string? wanted, string token)
    {
        var expected = Tokens(wanted);
        if (expected.Length == 0) return false;
        return Suffixes.Contains(token) || expected.Contains(token, StringComparer.Ordinal) ||
            Match(expected[0], token, true) != "unknown" || Match(expected[^1], token, false) != "unknown";
    }

    private static string[] Tokens(string? value) => string.IsNullOrWhiteSpace(value) ? [] :
        DeterministicBookMatcher.NormalizeWords(AuthorNameForm.Canonical(value)).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !Suffixes.Contains(token)).ToArray();

    private static string Best(string expected, string[] observed, bool allowInitial) =>
        observed.Select(token => Match(expected, token, allowInitial))
            .OrderBy(match => match switch { "exact" => 0, "initial" => 1, "fuzzy" => 2, _ => 3 }).First();

    private static string Match(string expected, string actual, bool allowInitial)
    {
        if (expected == actual) return "exact";
        if (allowInitial && actual.Length == 1 && expected.Length > 1 && actual[0] == expected[0]) return "initial";
        return IsFuzzyCandidate(expected, actual) && EditDistance(expected, actual) == 1 ? "fuzzy" : "unknown";
    }

    // One edit only. Names of five or more letters may differ anywhere; a name of four letters
    // (or a three-letter form of one) only when the first letter agrees. Shorter names are never fuzzed.
    private static bool IsFuzzyCandidate(string expected, string actual)
    {
        var shorter = Math.Min(expected.Length, actual.Length);
        var longer = Math.Max(expected.Length, actual.Length);
        if (longer > 64 || longer - shorter > 1) return false;
        return shorter >= 5 || shorter >= 3 && longer >= 4 && expected[0] == actual[0];
    }

    /// <summary>Optimal string alignment: insertion, deletion, substitution or one adjacent transposition each cost one.</summary>
    internal static int EditDistance(string expected, string actual)
    {
        var table = new int[expected.Length + 1, actual.Length + 1];
        for (var i = 0; i <= expected.Length; i++) table[i, 0] = i;
        for (var j = 0; j <= actual.Length; j++) table[0, j] = j;
        for (var i = 1; i <= expected.Length; i++)
        {
            for (var j = 1; j <= actual.Length; j++)
            {
                var cost = expected[i - 1] == actual[j - 1] ? 0 : 1;
                table[i, j] = Math.Min(Math.Min(table[i - 1, j] + 1, table[i, j - 1] + 1), table[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && expected[i - 1] == actual[j - 2] && expected[i - 2] == actual[j - 1])
                    table[i, j] = Math.Min(table[i, j], table[i - 2, j - 2] + 1);
            }
        }
        return table[expected.Length, actual.Length];
    }
}
