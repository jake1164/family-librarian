using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Resolves the transport CWA settings currently call for — <c>Local</c> or
/// <c>Sftp</c> — including decrypting whatever secrets that transport needs.
/// </summary>
public interface ICwaIngestTransportFactory
{
    ICwaIngestTransport Create(CwaSettings settings);
}
