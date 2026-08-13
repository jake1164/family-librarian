// Each test class owns a database and a host, but the container is shared and
// several tests boot a second host over the same data. Run serially so a failure
// means a real defect rather than two classes colliding.
[assembly: DoNotParallelize]

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Owns the lifetime of the shared PostgreSQL container.
/// </summary>
[TestClass]
public sealed class AssemblyHooks
{
    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        await PostgresFixture.StartAsync();
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync() => await PostgresFixture.StopAsync();
}
