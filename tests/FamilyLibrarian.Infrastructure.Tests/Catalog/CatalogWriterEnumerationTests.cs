using System.Text.RegularExpressions;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The catalog advisory lock only coordinates the writers that take it, so the set of production code that changes catalog rows
/// is pinned here: adding a writer is a deliberate, reviewed change that must join the lock, never a side effect.
/// </summary>
[TestClass]
public sealed partial class CatalogWriterEnumerationTests
{
    /// <summary>Calls that add, remove or retire catalog rows or change their guarded values.</summary>
    [GeneratedRegex(@"\.(AddWork|AddAuthor|AddSeries|AddExternalReference)\(|\.Retire\(|(ExternalReferences|Editions|Works|Authors|Series)\.(Add|AddRange|Remove|RemoveRange)\(|\.SetCatalogLength\(|\bseries\.MarkCompleted\(")]
    private static partial Regex CatalogMutation();

    private static readonly string[] ExpectedWriters = ["CatalogRepository.cs", "CatalogWorkResolver.cs", "OrphanedWorkRetirement.cs"];

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FamilyLibrarian.slnx")))
            {
                return Path.Combine(directory.FullName, "src");
            }
        }

        throw new AssertInconclusiveException("The repository source tree was not found next to the test output.");
    }

    [TestMethod]
    public void OnlyTheCatalogResolverAndOrphanRetirementChangeCatalogRowsAndBothJoinTheOneLock()
    {
        var files = Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}FamilyLibrarian.Domain{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => CatalogMutation().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The Domain entities define these operations and are excluded; the repository adapter forwards them; the two real writers are the resolver
        // (through the write scope's session) and the retirement sweep.
        CollectionAssert.AreEqual(
            ExpectedWriters,
            files,
            "A new production file changes catalog rows. It must join the catalog write lock (CatalogWriteLock) and be added here deliberately.");

        var sourceRoot = SourceRoot();
        Assert.Contains("CatalogWriteLock.BeginAsync", File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "OrphanedWorkRetirement.cs", SearchOption.AllDirectories).Single()),
            "Retirement takes the catalog lock before it evaluates its predicate.");
        Assert.Contains("CatalogWriteLock.BeginAsync", File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "PostgresCatalogWriteScope.cs", SearchOption.AllDirectories).Single()),
            "The resolver's write scope takes the same catalog lock.");
        var resolver = File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "CatalogWorkResolver.cs", SearchOption.AllDirectories).Single());
        Assert.Contains("writeScope.ExecuteAsync", resolver, "Every resolver mutation runs inside the write scope.");
    }

    [TestMethod]
    public void NoProcessLocalGateRemainsInTheResolverOrTheChatTools()
    {
        var sourceRoot = SourceRoot();
        foreach (var file in new[] { "CatalogWorkResolver.cs", "ConversationBookTools.cs" })
        {
            var text = File.ReadAllText(Directory.EnumerateFiles(sourceRoot, file, SearchOption.AllDirectories).Single());
            Assert.DoesNotContain("SemaphoreSlim", text, $"{file} must not serialize writers in-process; the database lock is the only gate.");
            Assert.DoesNotContain("KeyedAsyncLock", text, $"{file} must not serialize writers in-process; the database lock is the only gate.");
            Assert.DoesNotContain("CreationGate", text, file);
            Assert.DoesNotContain("ResolveLocks", text, file);
        }
    }

    [TestMethod]
    public void TheCatalogLockKeyIsDistinctFromTheRequestLockKey()
    {
        var sourceRoot = SourceRoot();
        var catalog = File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "CatalogWriteLock.cs", SearchOption.AllDirectories).Single());
        var requests = File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "RequestRepository.cs", SearchOption.AllDirectories).Single());
        Assert.Contains("Namespace = 831705", catalog);
        Assert.Contains("pg_advisory_xact_lock({831704}", requests, "The request lock namespace is unchanged.");
    }
}
