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

    internal void SetStatus(RequestFormatStatus status, DateTimeOffset atUtc)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        UpdatedAtUtc = atUtc;
    }
}
