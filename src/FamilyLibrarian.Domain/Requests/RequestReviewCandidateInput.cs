namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// The evidence a caller supplies for one <see cref="RequestReviewCandidate"/>.
/// </summary>
/// <remarks>
/// A named record rather than the ten-element tuple this replaced: the tuple
/// had four adjacent <c>string?</c> fields, and the defect that prompted this
/// work was precisely a call site passing the wrong strings into them.
/// </remarks>
/// <param name="Title">
/// The title the <em>source</em> claimed, not the requested one. Empty only
/// when the source named none, in which case <paramref name="TitleIsRequestFallback"/>
/// is set and a caller supplies the catalog title.
/// </param>
/// <param name="ReleaseName">The source's raw release name; administrator-only.</param>
/// <param name="AdminInspectionUri">The source's own record page; administrator-only.</param>
/// <param name="TitleIsRequestFallback">
/// Whether <paramref name="Title"/> came from the request rather than the source.
/// </param>
public sealed record RequestReviewCandidateInput(
    Guid RequestFormatId,
    string ProviderId,
    string ProviderResultId,
    string Title,
    string? Author,
    string? Language,
    string? Details,
    string? AdminInspectionUri,
    string? ReleaseName,
    bool TitleIsRequestFallback);
