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
    [GeneratedRegex(@"\.(AddWork|AddAuthor|AddSeries|AddExternalReference)\(|\bcatalog\.(AddEdition|AddSeriesEntry|AddClaim|RemoveClaim|AddObservation|RemoveObservation)\(|\.Retire\(|(ExternalReferences|Editions|Works|Authors|Series|SeriesEntries|MetadataClaims|SourceObservations)\.(Add|AddRange|Remove|RemoveRange)\(|\.SetCatalogLength\(|\.ReplaceCatalogPageCount\(|\.SetFirstPublication\(|\bseries\.MarkCompleted\(")]
    private static partial Regex CatalogMutation();

    private static readonly string[] ExpectedWriters =
        ["CatalogObservationService.cs", "CatalogRepository.cs", "ClaimLedger.cs", "OrphanedWorkRetirement.cs"];

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
    public void OnlyTheObservationServiceItsLedgerAndOrphanRetirementChangeCatalogRowsAndBothJoinTheOneLock()
    {
        var files = Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}FamilyLibrarian.Domain{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => CatalogMutation().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The Domain entities define these operations and are excluded; the repository adapter forwards them; the real writers are the single
        // observation service and its claim ledger (both only inside the write scope's session) and the retirement sweep.
        CollectionAssert.AreEqual(
            ExpectedWriters,
            files,
            "A new production file changes catalog rows. It must join the catalog write lock (CatalogWriteLock) and be added here deliberately.");

        var sourceRoot = SourceRoot();
        Assert.Contains("CatalogWriteLock.BeginAsync", File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "OrphanedWorkRetirement.cs", SearchOption.AllDirectories).Single()),
            "Retirement takes the catalog lock before it evaluates its predicate.");
        Assert.Contains("CatalogWriteLock.BeginAsync", File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "PostgresCatalogWriteScope.cs", SearchOption.AllDirectories).Single()),
            "The resolver's write scope takes the same catalog lock.");
        var observation = File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "CatalogObservationService.cs", SearchOption.AllDirectories).Single());
        Assert.Contains("writeScope.ExecuteAsync", observation, "Every observation mutation runs inside the write scope.");
        Assert.DoesNotContain("writeScope.ExecuteAsync", File.ReadAllText(Directory.EnumerateFiles(sourceRoot, "CatalogWorkResolver.cs", SearchOption.AllDirectories).Single()),
            "The resolver owns no writing of its own: it only delegates to the observation service.");
    }

    [TestMethod]
    public void NoProcessLocalGateRemainsInTheResolverOrTheChatTools()
    {
        var sourceRoot = SourceRoot();
        foreach (var file in new[] { "CatalogWorkResolver.cs", "CatalogObservationService.cs", "ClaimLedger.cs", "ConversationBookTools.cs" })
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

    /// <summary>
    /// Search, browse and the read-only conversation tools must be unable to reach the observation path: a read can never write
    /// durable catalog identity or evidence, and nothing there may call a provider's record into the catalog.
    /// </summary>
    [TestMethod]
    public void SearchBrowseAndReadOnlyToolsCannotReachTheObservationPath()
    {
        var sourceRoot = SourceRoot();
        string[] readOnly =
        [
            "CatalogSearchService.cs", "CatalogBrowseService.cs", "CatalogBrowseEndpoints.cs", "CatalogBrowseNavigation.cs",
            "CatalogBrowseRepository.cs", "SeriesPositionService.cs", "ExternalSearchCoalescer.cs", "CatalogSearchRunCoordinator.cs"
        ];
        foreach (var file in readOnly)
        {
            var path = Directory.EnumerateFiles(sourceRoot, file, SearchOption.AllDirectories).SingleOrDefault();
            Assert.IsNotNull(path, $"{file} should exist so this guard cannot pass vacuously.");
            var text = File.ReadAllText(path);
            foreach (var forbidden in new[] { "CatalogObservationService", "CatalogWorkResolver", "ObservationIntent", "ICatalogWriteScope", "ClaimLedger" })
            {
                Assert.DoesNotContain(forbidden, text, $"{file} is a read path and must not reach {forbidden}.");
            }
        }
    }
}
