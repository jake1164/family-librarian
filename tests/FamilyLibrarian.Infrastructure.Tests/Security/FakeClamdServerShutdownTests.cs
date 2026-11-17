namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// The fake clamd server's accept loop must treat a listener stopped underneath it as a normal shutdown. A full non-Release run once
/// failed a ClamAV scanner case because the loop threw <see cref="ObjectDisposedException"/> from its first accept when disposal won
/// the race, and the dispose then rethrew it into the test.
/// </summary>
[TestClass]
public sealed class FakeClamdServerShutdownTests
{
    [TestMethod]
    public async Task DisposingTheServerImmediatelyAfterStartingItNeverFaultsTheTest()
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var server = FakeClamdServer.Start();
            await server.DisposeAsync();
        }
    }
}
