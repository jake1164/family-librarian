namespace FamilyLibrarian.Domain.Requests;

/// <summary>
/// One requested media type on a <see cref="BookRequest"/>.
/// </summary>
/// <remarks>
/// Formats are rows rather than a flags enum because acquisition, security, and
/// delivery eventually resolve one format at a time, and a flags column cannot
/// carry per-format state or its own history.
/// </remarks>
public sealed class RequestFormat
{
    private RequestFormat()
    {
    }

    internal RequestFormat(
        Guid requestId,
        RequestMediaType mediaType,
        DateTimeOffset createdAtUtc)
    {
        RequestId = requestId;
        MediaType = mediaType;
        Status = RequestFormatStatus.Requested;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestId { get; private set; }

    public BookRequest Request { get; private set; } = null!;

    public RequestMediaType MediaType { get; private set; }

    public RequestFormatStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    /// <summary>
    /// Non-null once the requester (or an admin) explicitly accepted a
    /// non-English <see cref="RequestReviewCandidate"/> for this format ("get
    /// it anyway"). Downstream identity/destination verification for this
    /// specific format must treat this as a standing exception to the
    /// ordinary English-or-unspecified language filter.
    /// </summary>
    public string? AcceptedLanguage { get; private set; }

    internal void SetStatus(RequestFormatStatus status, DateTimeOffset atUtc)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        UpdatedAtUtc = atUtc;
    }

    internal void AcceptLanguage(string? language) => AcceptedLanguage = language;
}
