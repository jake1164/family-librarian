namespace FamilyLibrarian.Domain.Requests;

/// <summary>A person's interest in a shared request; notes remain private to that person and librarians.</summary>
public sealed class RequestParticipant
{
    private RequestParticipant() { }

    internal RequestParticipant(Guid requestId, Guid userId, IEnumerable<RequestMediaType> formats,
        string? note, DateTimeOffset atUtc, Guid? deliveryTargetId = null)
    {
        RequestId = requestId;
        UserId = userId;
        JoinedAtUtc = atUtc;
        Join(formats, note, deliveryTargetId);
    }

    public Guid RequestId { get; private set; }
    public Guid UserId { get; private set; }
    public bool WantsEbook { get; private set; }
    public bool WantsAudiobook { get; private set; }
    public string? Note { get; private set; }

    /// <summary>
    /// The <c>DeliveryTarget</c> this participant asked to receive the ebook
    /// on (e.g. "send to my Kindle"), if any -- snapshotted at request/join
    /// time per the same rule as <see cref="WantsEbook"/>/<see cref="WantsAudiobook"/>:
    /// changing delivery settings later must not alter an existing request.
    /// Only meaningful alongside <see cref="WantsEbook"/> -- Kindle delivery
    /// has no audiobook equivalent today.
    /// </summary>
    public Guid? DeliveryTargetId { get; private set; }

    public DateTimeOffset JoinedAtUtc { get; private set; }
    public DateTimeOffset? WithdrawnAtUtc { get; private set; }

    internal void Join(IEnumerable<RequestMediaType> formats, string? note, Guid? deliveryTargetId = null)
    {
        var includesEbook = false;
        foreach (var format in formats)
        {
            if (format == RequestMediaType.Ebook)
            {
                WantsEbook = true;
                includesEbook = true;
            }
            else if (format == RequestMediaType.Audiobook) WantsAudiobook = true;
            else throw new ArgumentException("Unknown requested format.", nameof(formats));
        }

        if (deliveryTargetId is not null && !WantsEbook)
            throw new ArgumentException("Kindle delivery requires the ebook format.", nameof(deliveryTargetId));

        if (note?.Trim().Length > BookRequest.MaxNoteLength)
            throw new ArgumentException("The requester note is too long.", nameof(note));
        if (!string.IsNullOrWhiteSpace(note)) Note = note.Trim();
        if (includesEbook) DeliveryTargetId = deliveryTargetId;
        WithdrawnAtUtc = null;
    }

    internal void Withdraw(DateTimeOffset atUtc) => WithdrawnAtUtc = atUtc;
}
