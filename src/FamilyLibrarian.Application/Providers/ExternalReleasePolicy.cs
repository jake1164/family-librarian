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
    public const string UnknownDrmConfirmationReason =
        "The provider could not confirm that this ebook source is DRM-free.";

    public static ExternalReleaseVerdict Evaluate(ExternalProviderReleaseEvidence? release, RequestMediaType mediaType)
    {
        if (mediaType == RequestMediaType.Ebook)
        {
            if (release is null)
            {
                return ExternalReleaseVerdict.Rejected(
                    "The provider did not report a source ebook format or DRM state.");
            }

            if (release.DrmStatus == ExternalProviderDrmStatus.Encrypted)
            {
                return ExternalReleaseVerdict.Rejected(
                    "This release is reported as encrypted or DRM-protected.");
            }

            var formatTier = ExternalEbookFormatPolicy.Classify(release.Format);
            if (formatTier == ExternalEbookFormatTier.Reject)
            {
                return ExternalReleaseVerdict.Rejected(
                    $"'{release.Format ?? "unknown"}' is not an accepted ebook source format.");
            }

            if (release.IsCollection == true)
            {
                return ExternalReleaseVerdict.ReviewRequired(
                    "This release is reported as a multi-book collection, not the single title requested.");
            }

            if (release.IsSample == true)
            {
                return ExternalReleaseVerdict.ReviewRequired(
                    "This release is reported as a sample/preview, not the complete work.");
            }

            if (formatTier == ExternalEbookFormatTier.Possible)
            {
                return ExternalReleaseVerdict.ReviewRequired(
                    "This source format needs an explicit review before CWA conversion.");
            }

            if (release.DrmStatus == ExternalProviderDrmStatus.Unknown)
            {
                return ExternalReleaseVerdict.ReviewRequired(UnknownDrmConfirmationReason);
            }

            return ExternalReleaseVerdict.Acceptable;
        }

        if (release is null)
        {
            return ExternalReleaseVerdict.Acceptable;
        }

        if (release.IsCollection == true)
        {
            return ExternalReleaseVerdict.ReviewRequired(
                "This release is reported as a multi-book collection, not the single title requested.");
        }

        if (release.IsSample == true)
        {
            return ExternalReleaseVerdict.ReviewRequired(
                "This release is reported as a sample/preview, not the complete work.");
        }

        // Only audiobooks have a meaningful abridged/unabridged distinction
        // for FL's purposes today. A release explicitly reported abridged
        // and not also reported unabridged is a mismatch worth a human's
        // attention -- an ebook release reporting the same flags is not
        // acted on here (nothing downstream distinguishes abridged/
        // unabridged ebook editions yet).
        if (mediaType == RequestMediaType.Audiobook && release.IsAbridged == true && release.IsUnabridged != true)
        {
            return ExternalReleaseVerdict.ReviewRequired("This release is reported as abridged.");
        }

        return ExternalReleaseVerdict.Acceptable;
    }
}

public sealed record ExternalReleaseVerdict(bool IsRejected, bool RequiresConfirmation, string? Reason)
{
    public static readonly ExternalReleaseVerdict Acceptable = new(false, false, null);

    public static ExternalReleaseVerdict Rejected(string reason) => new(true, false, reason);

    public static ExternalReleaseVerdict ReviewRequired(string reason) => new(false, true, reason);
}
