namespace FamilyLibrarian.Application.Communications;

/// <summary>The outcome of one provider's attempt to send an <see cref="Domain.Communications.OutboundCommunication"/>.</summary>
public sealed record SendResult(bool Succeeded, string? Error)
{
    public static SendResult Success() => new(true, null);

    public static SendResult Failure(string error) => new(false, error);
}
