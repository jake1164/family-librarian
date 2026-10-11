using System.Reflection;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Infrastructure.ModelTesting;
using FamilyLibrarian.Web.Communications;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Alpha.6 M4 defense in depth. Production code may reach a model only through <see cref="IModelExecutionPolicy"/>, which
/// enforces availability, budgets, size limits and failure handling. The administrator's manual capability test is the one
/// other caller (their private nested helpers are part of them). Any new class that takes an <see cref="IModelClient"/> must fail this test and be reviewed.
/// </summary>
[TestClass]
public sealed class ModelClientUseGuardTests
{
    [TestMethod]
    public void OnlyThePolicyAndTheManualCapabilityTestMayHoldAModelClient()
    {
        Assembly[] productAssemblies =
        [
            typeof(IModelClient).Assembly,
            typeof(DependencyInjection).Assembly,
            typeof(MatrixInboundSyncHostedService).Assembly
        ];

        var holders = productAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false } && !typeof(IModelClient).IsAssignableFrom(type))
            .Where(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IModelClient))))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { typeof(ModelCapabilityTestService).FullName, typeof(ModelExecutionPolicy).FullName }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            holders);
    }

    [TestMethod]
    public void ModelClientImplementationsLiveOnlyInTheInfrastructureGatewayClient()
    {
        var implementations = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IModelClient).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToArray();

        CollectionAssert.AreEqual(new[] { typeof(OpenAiCompatibleModelClient).FullName }, implementations);
    }
}
