namespace FamilyLibrarian.Application.Matching;

public enum IdentityEvidenceState { Unknown, Exact, Compatible, Partial, Fuzzy, Conflicting }
public enum WorkIdentityDecision { Match, MatchWithConditions, Mismatch, Ambiguous }

/// <summary>A contiguous local title window, never a whole-release similarity score.</summary>
public sealed record ReleaseTitleEvidence(
    IdentityEvidenceState State, string Expected, string? Observed, int Start, int Length, string Reason)
{
    public bool IsPositive => State is IdentityEvidenceState.Exact or IdentityEvidenceState.Compatible or IdentityEvidenceState.Fuzzy;
}

public static class ReleaseTitleMatcher
{
    // Only grammatical words, not release descriptors. Ordering is preserved.
    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "THE", "A", "AN", "OF", "AND" };

    public static string[] Tokens(string value) => DeterministicBookMatcher.NormalizeWords(value)
        .ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static ReleaseTitleEvidence Evaluate(string expected, IReadOnlyList<string> tokens)
    {
        var wanted = Tokens(expected);
        if (wanted.Length == 0) return new(IdentityEvidenceState.Unknown, expected, null, -1, 0, "Requested title is absent.");
        for (var start = 0; start + wanted.Length <= tokens.Count; start++)
            if (wanted.SequenceEqual(tokens.Skip(start).Take(wanted.Length)))
                return Evidence(IdentityEvidenceState.Exact, start, wanted.Length, "Exact normalized title phrase.");

        var significant = wanted.Where(token => !Articles.Contains(token)).ToArray();
        // A one-word reduced title may only drop a leading/trailing article.
        var reduced = Tokens(DeterministicBookMatcher.RemoveArticleVariants(expected));
        if (!wanted.SequenceEqual(reduced) && reduced.Length > 0)
            for (var start = 0; start + reduced.Length <= tokens.Count; start++)
                if (reduced.SequenceEqual(tokens.Skip(start).Take(reduced.Length)))
                    return Evidence(IdentityEvidenceState.Compatible, start, reduced.Length, "Title with its article omitted.");
        if (significant.Length >= 2)
            for (var start = 0; start < tokens.Count; start++)
                for (var length = Math.Max(2, significant.Length); length <= wanted.Length + 2 && start + length <= tokens.Count; length++)
                    if (!Articles.Contains(tokens[start]) && !Articles.Contains(tokens[start + length - 1]) &&
                        significant.SequenceEqual(tokens.Skip(start).Take(length).Where(token => !Articles.Contains(token))))
                        return Evidence(IdentityEvidenceState.Compatible, start, length, "Ordered title tokens with grammatical words varied.");

        // One typo in one token, with at least one exact distinctive anchor.
        // Single-word and very short titles are deliberately never fuzzy.
        if (wanted.Length >= 2 && wanted.Sum(token => token.Length) >= 8)
            for (var start = 0; start + wanted.Length <= tokens.Count; start++)
            {
                var edits = 0;
                var anchored = false;
                var valid = true;
                for (var offset = 0; offset < wanted.Length; offset++)
                {
                    var actual = tokens[start + offset];
                    if (wanted[offset] == actual)
                        anchored |= wanted[offset].Length >= 4 && !Articles.Contains(actual);
                    else if (OneEdit(wanted[offset], actual)) edits++;
                    else { valid = false; break; }
                }
                if (valid && anchored && edits == 1)
                    return Evidence(IdentityEvidenceState.Fuzzy, start, wanted.Length, "One character edit in a local title window with an exact anchor.");
            }
        return new(IdentityEvidenceState.Unknown, expected, null, -1, 0, "No complete local title identity established.");

        ReleaseTitleEvidence Evidence(IdentityEvidenceState state, int start, int length, string reason) =>
            new(state, expected, string.Join(' ', tokens.Skip(start).Take(length)), start, length, reason);
    }

    private static bool OneEdit(string expected, string actual)
    {
        if (expected.Length < 4 || actual.Length < 3 || Math.Max(expected.Length, actual.Length) > 64 ||
            Math.Abs(expected.Length - actual.Length) > 1) return false;
        var left = 0;
        var right = 0;
        var edits = 0;
        while (left < expected.Length && right < actual.Length)
        {
            if (expected[left] == actual[right]) { left++; right++; continue; }
            if (++edits > 1) return false;
            if (expected.Length >= actual.Length) left++;
            if (actual.Length >= expected.Length) right++;
        }
        return edits + expected.Length - left + actual.Length - right == 1;
    }
}
