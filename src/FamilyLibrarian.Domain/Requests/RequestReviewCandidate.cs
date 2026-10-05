namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// One plausible candidate offered to the requester for a
/// <see cref="RequestReviewCategory.PreferenceAmbiguity"/> review (SELFSERV-1).
/// Holds title/author/edition-distinguishing details only, deliberately never
/// provider-internal data beyond the identifiers needed to re-acquire it.
/// </summary>
public sealed class RequestReviewCandidate
{
    private RequestReviewCandidate()
    {
    }

    internal RequestReviewCandidate(
        Guid requestId, Guid requestFormatId, string providerId, string providerResultId, string title,
        string? author, string? language, string? details, string? adminInspectionUri,
        string? releaseName, bool titleIsRequestFallback,
        int displayOrder, DateTimeOffset createdAtUtc)
    {
        RequestId = requestId;
        RequestFormatId = requestFormatId;
        ProviderId = providerId;
        ProviderResultId = providerResultId;
        Title = title;
        Author = author;
        Language = language;
        Details = details;
        AdminInspectionUri = adminInspectionUri;
        ReleaseName = releaseName;
        TitleIsRequestFallback = titleIsRequestFallback;
        DisplayOrder = displayOrder;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    /// <summary>Which requested format this candidate was found for -- needed to re-drive acquisition if accepted.</summary>
    public Guid RequestFormatId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public string ProviderResultId { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string? Author { get; private set; }

    public string? Language { get; private set; }

    /// <summary>Neutral edition/release facts for the requester; never provider provenance.</summary>
    public string? Details { get; private set; }

    /// <summary>
    /// A provider-declared browser page retained exclusively for an
    /// administrator to inspect this candidate. Requester projections must
    /// never expose it.
    /// </summary>
    public string? AdminInspectionUri { get; private set; }

    /// <summary>
    /// The source's own raw release name, retained exclusively for an
    /// administrator. For an indexer-backed source this is frequently the only
    /// field that distinguishes one record from another -- without it a review
    /// of nine releases of the same book showed nine identical rows differing
    /// only in byte size. Requester projections must never expose it.
    /// </summary>
    public string? ReleaseName { get; private set; }

    /// <summary>
    /// True when <see cref="Title"/> came from the request rather than from
    /// the source, because the source named no title of its own. Reviews used
    /// to store the requested title for <em>every</em> candidate, which made a
    /// list of unrelated books read as confirmed copies of the requested one;
    /// recording the substitution keeps that from being invisible again.
    /// </summary>
    public bool TitleIsRequestFallback { get; private set; }

    public int DisplayOrder { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
