using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// Builds a <see cref="RequestReviewCandidateInput"/> from a provider result.
/// </summary>
/// <remarks>
/// Both review paths used to overwrite every candidate's title and author
/// with the <em>request's</em>, so a list of unrelated records rendered as
/// that many identical rows all claiming to be the requested book — directly
/// beneath a reason saying no title could be confirmed, and above a button
/// whose acceptance waives post-download identity verification. The source's
/// own claim is kept instead. The catalog title remains the fallback for a
/// source that named nothing at all, applied in one place by
/// <see cref="WithCatalogFallback"/> and flagged so it is never silent.
/// </remarks>
public static class RequestReviewCandidateRecord
{
    public static RequestReviewCandidateInput From(Guid requestFormatId, FulfillmentOption option)
    {
        var sourceTitle = Trimmed(option.Title);
        return new RequestReviewCandidateInput(
            requestFormatId,
            option.ProviderId,
            option.ProviderResultId,
            sourceTitle ?? string.Empty,
            Trimmed(option.Author),
            option.Language,
            RequestReviewCandidatePresentation.BuildDetails(option),
            option.AdminInspectionUri?.ToString(),
            Trimmed(option.ReleaseName),
            sourceTitle is null);
    }

    /// <summary>
    /// Supplies the catalog title/author for a candidate whose source named
    /// none. Every other candidate is returned untouched.
    /// </summary>
    public static RequestReviewCandidateInput WithCatalogFallback(
        RequestReviewCandidateInput candidate, string workTitle, string? workAuthor) =>
        candidate.TitleIsRequestFallback
            ? candidate with { Title = workTitle, Author = candidate.Author ?? workAuthor }
            : candidate;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
