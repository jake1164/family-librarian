// Bound concurrent host construction independently of the machine's CPU count.
// Each class owns its database; methods stay sequential because some restart a
// host over that database. Classes sharing process-wide state opt out explicitly.
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel, Workers = 8)]

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
