using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Family Librarian's own release-policy decision (protocol v2 §7/§10/§16)
/// — a provider supplies release evidence, FL decides whether the release
/// is acceptable to fetch, never the reverse. Deliberately independent of
/// match confidence: a release problem can disqualify even a perfect
/// identifier match — a correct ISBN attached to an omnibus edition is
/// still an omnibus, not the single title requested.
/// </summary>
public static class ExternalReleasePolicy
{
    public static ExternalReleaseVerdict Evaluate(ExternalProviderReleaseEvidence? release, RequestMediaType mediaType)
    {
        if (release is null)
        {
            return ExternalReleaseVerdict.Acceptable;
        }

        if (release.IsCollection == true)
        {
            return new ExternalReleaseVerdict(
                true, "This release is reported as a multi-book collection, not the single title requested.");
        }

        if (release.IsSample == true)
        {
            return new ExternalReleaseVerdict(
                true, "This release is reported as a sample/preview, not the complete work.");
        }

        // Only audiobooks have a meaningful abridged/unabridged distinction
        // for FL's purposes today. A release explicitly reported abridged
        // and not also reported unabridged is a mismatch worth a human's
        // attention -- an ebook release reporting the same flags is not
        // acted on here (nothing downstream distinguishes abridged/
        // unabridged ebook editions yet).
        if (mediaType == RequestMediaType.Audiobook && release.IsAbridged == true && release.IsUnabridged != true)
        {
            return new ExternalReleaseVerdict(true, "This release is reported as abridged.");
        }

        return ExternalReleaseVerdict.Acceptable;
    }
}

public sealed record ExternalReleaseVerdict(bool RequiresConfirmation, string? Reason)
{
    public static readonly ExternalReleaseVerdict Acceptable = new(false, null);
}
