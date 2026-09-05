namespace FamilyLibrarian.Domain.Requests;

/// <summary>A person's interest in a shared request; notes remain private to that person and librarians.</summary>
public sealed class RequestParticipant
{
    private RequestParticipant() { }

    internal RequestParticipant(Guid requestId, Guid userId, IEnumerable<RequestMediaType> formats,
        string? note, DateTimeOffset atUtc)
    {
        RequestId = requestId;
        UserId = userId;
        JoinedAtUtc = atUtc;
        Join(formats, note);
    }

    public Guid RequestId { get; private set; }
    public Guid UserId { get; private set; }
    public bool WantsEbook { get; private set; }
    public bool WantsAudiobook { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }
    public DateTimeOffset? WithdrawnAtUtc { get; private set; }

    internal void Join(IEnumerable<RequestMediaType> formats, string? note)
    {
        foreach (var format in formats)
        {
            if (format == RequestMediaType.Ebook) WantsEbook = true;
            else if (format == RequestMediaType.Audiobook) WantsAudiobook = true;
            else throw new ArgumentException("Unknown requested format.", nameof(formats));
        }

        if (note?.Trim().Length > BookRequest.MaxNoteLength)
            throw new ArgumentException("The requester note is too long.", nameof(note));
        if (!string.IsNullOrWhiteSpace(note)) Note = note.Trim();
        WithdrawnAtUtc = null;
    }

    internal void Withdraw(DateTimeOffset atUtc) => WithdrawnAtUtc = atUtc;
}
