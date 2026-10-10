using System.Reflection;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Web.Communications;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Alpha.6 M1-0 guard. <see cref="BookMatchService"/> promotes any non-null
/// <see cref="IAmbiguityResolver"/> pick straight to a Match, so until the contract
/// is redefined (D-7, M4-2) no resolver other than <see cref="NoOpAmbiguityResolver"/>
/// may exist anywhere in the product assemblies. Adding one must fail this test and
/// force the contract decision first. The contract defect itself is pinned as
/// KnownGap R01 in Infrastructure.Tests.
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
