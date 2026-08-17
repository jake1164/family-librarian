namespace FamilyLibrarian.Domain.Tests.Architecture;

[TestClass]
public sealed class DomainBoundaryTests
{
    [TestMethod]
    public void DomainAssemblyDoesNotReferenceApplicationOrInfrastructureLayers()
    {
        var references = typeof(RoleNames).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .ToArray();

        // Matched by prefix, not equality: the assemblies that would actually
        // show up here are named Microsoft.EntityFrameworkCore.Relational and
        // Microsoft.AspNetCore.Http.Abstractions, so an equality check would let
        // every sub-assembly through while appearing to forbid the whole family.
        var forbiddenPrefixes = new[]
        {
            "FamilyLibrarian.Application",
            "FamilyLibrarian.Contracts",
            "FamilyLibrarian.Infrastructure",
            "FamilyLibrarian.Web",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "MudBlazor",
            "Npgsql",
            "Renci.SshNet"
        };

        CollectionAssert.Contains(
            references,
            "System.Runtime",
            "The Domain assembly reference list looks wrong; the assertion below would be vacuous.");

        var violations = references
            .Where(reference => forbiddenPrefixes.Any(
                prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            0,
            violations.Length,
            $"Domain must not depend on any outer layer or vendor SDK. Found: {string.Join(", ", violations)}");
    }
}
