using System.Text.RegularExpressions;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// A browser class that is in the Release category but guarded by an opt-in flag silently reports every case as inconclusive when
/// the Release workflow never sets that flag. This pins the wiring: every opt-in flag a Release-category test class reads must be
/// enabled, with the value the class checks for, in the workflow step that runs the Release category.
/// </summary>
[TestClass]
public sealed partial class ReleaseWorkflowOptInTests
{
    [GeneratedRegex("\"(FAMILY_LIBRARIAN_[A-Z_]+_BROWSER_TESTS)\"")]
    private static partial Regex OptInFlag();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FamilyLibrarian.slnx"))) return directory.FullName;
        }

        throw new AssertInconclusiveException("The repository root was not found next to the test output.");
    }

    [TestMethod]
    public void EveryOptInFlagReadByAReleaseCategoryClassIsEnabledInTheReleaseWorkflow()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-tests.yml"));

        var flags = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => file.Text.Contains("[TestCategory(\"Release\")]", StringComparison.Ordinal))
            .SelectMany(file => OptInFlag().Matches(file.Text).Select(match => (Flag: match.Groups[1].Value, File: Path.GetFileName(file.Path))))
            .ToArray();

        Assert.IsTrue(flags.Any(flag => flag.File == "SourceLookupBrowserTests.cs"),
            "The source-lookup browser class and its flag were found, so this guard cannot pass vacuously.");
        Assert.IsTrue(flags.Any(flag => flag.File == "CatalogBrowseBrowserTests.cs"), "The existing browse browser class was found too.");

        foreach (var (flag, file) in flags.Distinct())
        {
            Assert.IsTrue(
                Regex.IsMatch(workflow, $"^\\s*{flag}:\\s*\"1\"\\s*$", RegexOptions.Multiline),
                $"{file} reads {flag}, but .github/workflows/release-tests.yml does not set it to \"1\", so its cases would all be inconclusive in CI.");
        }

        Assert.Contains("--filter \"TestCategory=Release\"", workflow, "The workflow step that sets the flags runs the Release category.");
    }
}
