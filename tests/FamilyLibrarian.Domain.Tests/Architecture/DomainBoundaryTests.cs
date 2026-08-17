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

        var forbiddenReferences = new[]
        {
            "FamilyLibrarian.Application",
            "FamilyLibrarian.Contracts",
            "FamilyLibrarian.Infrastructure",
            "FamilyLibrarian.Web",
            "FamilyLibrarian.Web.Client",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "MudBlazor",
            "Npgsql"
        };

        var violations = references
            .Where(reference => forbiddenReferences.Contains(reference, StringComparer.Ordinal))
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }
}
