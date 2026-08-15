using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe CWA transport fake: no real filesystem/SFTP write ever happens in the ordinary test suite.</summary>
internal sealed class AlwaysSucceedsCwaIngestTransport : ICwaIngestTransport
{
    public Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class AlwaysSucceedsCwaIngestTransportFactory : ICwaIngestTransportFactory
{
    public ICwaIngestTransport Create(CwaSettings settings) => new AlwaysSucceedsCwaIngestTransport();
}
