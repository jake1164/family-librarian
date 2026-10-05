namespace FamilyLibrarian.Application.Matching;

public enum AuthorAffinityKind { Conflict, Unknown, Fuzzy, FirstOnly, FirstExactLastFuzzy, LastOnly, LastExactFirstFuzzy, Compatible, Exact }

/// <summary>Supporting identity evidence, never a provider popularity or acceptance score.</summary>
public sealed record AuthorAffinityResult(
    AuthorAffinityKind Kind, int Score, string? WantedAuthor, string? DetectedAuthor,
    string FirstNameMatch, string LastNameMatch)
{
    public bool SupportsAutomaticIdentity => Kind is AuthorAffinityKind.Exact or AuthorAffinityKind.Compatible
        or AuthorAffinityKind.LastExactFirstFuzzy or AuthorAffinityKind.FirstExactLastFuzzy;
}

public static class AuthorAffinity
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal) { "JR", "SR", "II", "III", "IV" };

    public static AuthorAffinityResult Evaluate(string? wanted, string? detected, bool structured = true)
    {
        var expected = Tokens(Reorder(wanted));
        var observed = Tokens(detected);
        AuthorAffinityResult Result(AuthorAffinityKind kind, int score, string first = "unknown", string last = "unknown") =>
            new(kind, score, wanted, detected, first, last);
        if (expected.Length == 0 || observed.Length == 0)
            return Result(AuthorAffinityKind.Unknown, 0);
        if (expected.ToHashSet(StringComparer.Ordinal).SetEquals(observed))
            return Result(AuthorAffinityKind.Exact, 100, "exact", "exact");
        // Single-component requested names support exact evidence only.
        if (expected.Length < 2)
            return Result(structured ? AuthorAffinityKind.Conflict : AuthorAffinityKind.Unknown, structured ? -100 : 0);
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
        var expected = Tokens(Reorder(wanted));
        if (expected.Length == 0) return false;
        return Suffixes.Contains(token) || expected.Contains(token, StringComparer.Ordinal) ||
            Match(expected[0], token, true) != "unknown" || Match(expected[^1], token, false) != "unknown";
    }

    private static string? Reorder(string? name)
    {
        if (name is null) return null;
        var parts = name.Split(',');
        return parts.Length == 2 && !Suffixes.Contains(parts[1].Trim().TrimEnd('.').ToUpperInvariant())
            ? $"{parts[1]} {parts[0]}" : name;
    }

    private static string[] Tokens(string? value) => string.IsNullOrWhiteSpace(value) ? [] :
        DeterministicBookMatcher.NormalizeWords(value).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !Suffixes.Contains(token)).ToArray();

    private static string Best(string expected, string[] observed, bool allowInitial) =>
        observed.Select(token => Match(expected, token, allowInitial))
            .OrderBy(match => match switch { "exact" => 0, "initial" => 1, "fuzzy" => 2, _ => 3 }).First();

    private static string Match(string expected, string actual, bool allowInitial)
    {
        if (expected == actual) return "exact";
        if (allowInitial && actual.Length == 1 && expected.Length > 1 && actual[0] == expected[0]) return "initial";
        // One insertion/deletion/substitution only; short names are never fuzzed.
        if (Math.Min(expected.Length, actual.Length) < 5 || Math.Max(expected.Length, actual.Length) > 64 ||
            Math.Abs(expected.Length - actual.Length) > 1) return "unknown";
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        for (var i = 1; i <= expected.Length; i++)
        {
            var current = new int[actual.Length + 1];
            current[0] = i;
            for (var j = 1; j <= actual.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1] == 1 ? "fuzzy" : "unknown";
    }
}
