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
        string? author, string? language, int displayOrder, DateTimeOffset createdAtUtc)
    {
        RequestId = requestId;
        RequestFormatId = requestFormatId;
        ProviderId = providerId;
        ProviderResultId = providerResultId;
        Title = title;
        Author = author;
        Language = language;
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

    public int DisplayOrder { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
