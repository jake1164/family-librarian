using System.Reflection;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Web.Communications;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Alpha.6 defense in depth. Since the R01 safety fix <see cref="BookMatchService"/>
/// treats an <see cref="IAmbiguityResolver"/> pick as advisory and never promotes it
/// to a Match, so this is no longer the only protection. It still forbids any
/// resolver other than <see cref="NoOpAmbiguityResolver"/> in the product assemblies.
/// D-7 resolution (M4-2): model-backed semantic analysis does not implement this seam
/// at all. It produces separately stored, evidence-cited findings
/// (<c>FamilyLibrarian.Application.SemanticAssessment</c>) that cannot carry a candidate
/// or a decision, so there is nothing for a resolver to promote. Adding a resolver here
/// must still fail this test and force a deliberate review.
/// </summary>
[TestClass]
public sealed class AmbiguityResolverGuardTests
{
    [TestMethod]
    public void OnlyTheNoOpAmbiguityResolverExists()
    {
        Assembly[] productAssemblies =
        [
            typeof(IAmbiguityResolver).Assembly,
            typeof(DependencyInjection).Assembly,
            typeof(MatrixInboundSyncHostedService).Assembly
        ];

        var implementations = productAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IAmbiguityResolver).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(new[] { typeof(NoOpAmbiguityResolver).FullName }, implementations);
    }
}
