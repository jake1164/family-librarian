namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// Whether a source's own claimed title plausibly names a requested work.
/// </summary>
/// <remarks>
/// Presentation evidence for a librarian, not an acquisition decision: the
/// acquisition path has already decided what may be taken unattended, and
/// this never widens that. It exists because a review screen could not show
/// the difference between a source offering the requested book and a source
/// offering something else -- every candidate was relabeled with the request's
/// own title, so twenty-three unrelated records read as twenty-three copies of
/// the requested one.
/// <para>
/// It lives server-side because the WebAssembly client holds presentation only
/// and must not carry a second, drifting copy of the matching rules.
/// </para>
/// </remarks>
public static class WorkTitlePlausibility
{
    private static readonly DeterministicBookMatcher Matcher = new();

    public static bool NamesRequestedWork(string workTitle, string? workAuthor, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(workTitle) || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        // Either direction counts here, unlike acquisition: the catalog title
        // can carry edition packaging the source omits ("Moby Dick
        // (Illustrated Classics)" vs "Moby Dick"), and the source can carry
        // packaging the catalog omits ("Moby Dick" vs "Moby Dick; Or, The
        // Whale"). Both are the same work to a librarian's eye.
        return Matcher.TitleMatches(workTitle, candidateTitle, workAuthor) ||
            Matcher.TitleMatches(candidateTitle, workTitle, workAuthor);
    }
}
