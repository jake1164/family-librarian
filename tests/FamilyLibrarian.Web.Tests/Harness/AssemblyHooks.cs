// Classes run in parallel; tests within a class stay sequential, because several
// boot a second host over their class's database. Each class owns its database
// and host, the shared container only hands out clones, and host startup (the
// one process-wide step) is serialized in FamilyLibrarianAppFactory.CreateHost.
// A class that cannot share the machine opts out with [DoNotParallelize].
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel, Workers = 0)]

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
        if (PostgresFixture.UnavailableReason is null)
        {
            await PostgresFixture.PrepareSeededTemplateAsync(WebTestFixture.SeedTemplateAsync);
        }
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync() => await PostgresFixture.StopAsync();
}
