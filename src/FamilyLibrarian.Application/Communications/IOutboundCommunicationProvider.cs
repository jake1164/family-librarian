using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// A communications provider capable of sending. SMTP implements only this;
/// a future bidirectional provider (e.g. Matrix) would also implement an
/// inbound counterpart, without SMTP needing to know that capability exists.
/// </summary>
public interface IOutboundCommunicationProvider : ICommunicationProvider
{
    /// <summary>Whether this provider is currently configured and enabled.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);

    Task<SendResult> SendAsync(OutboundCommunication communication, CancellationToken cancellationToken);
}
