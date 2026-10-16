namespace FamilyLibrarian.Application.Communications;

/// <summary>The outcome of one provider's attempt to send an <see cref="Domain.Communications.OutboundCommunication"/>.</summary>
public sealed record SendResult(bool Succeeded, string? Error)
{
    public string? EventId { get; init; }

    public static SendResult Success() => new(true, null);

    public static SendResult Success(string? eventId) => new(true, null) { EventId = eventId };

    public static SendResult Failure(string error) => new(false, error);
}
