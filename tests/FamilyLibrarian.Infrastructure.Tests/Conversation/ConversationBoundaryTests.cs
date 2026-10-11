using System.Reflection;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// Pins the conversation's reach. Chat may search, read, list the caller's requests and create an ordinary request, and
/// nothing else: no approval, security override, publishing, import or acquisition services, and no administrative action.
/// Adding a dependency here is a deliberate, reviewed change.
/// </summary>
[TestClass]
public sealed class ConversationBoundaryTests
{
    private static readonly HashSet<Type> AllowedExternalDependencies =
    [
        typeof(ICurrentUser),
        typeof(IClock),
        typeof(ICatalogSearchService),
        typeof(IActiveMetadataProviderResolver),
        typeof(CatalogWorkResolver),
        typeof(BookRequestService),
        typeof(ICandidateAvailabilityService)
    ];

    private static IEnumerable<Type> ConversationTypes() =>
        typeof(IConversationService).Assembly.GetTypes()
            .Where(type => type.Namespace == "FamilyLibrarian.Application.Conversation" && !type.IsNested && !type.IsInterface);

    [TestMethod]
    public void EveryConversationTypeDependsOnlyOnTheAllowListedServices()
    {
        foreach (var type in ConversationTypes())
        {
            foreach (var parameter in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic).SelectMany(constructor => constructor.GetParameters()))
            {
                var parameterType = parameter.ParameterType;
                var allowed = parameterType.Namespace == "FamilyLibrarian.Application.Conversation" ||
                              AllowedExternalDependencies.Contains(parameterType) ||
                              parameterType.Namespace?.StartsWith("System", StringComparison.Ordinal) == true;
                Assert.IsTrue(allowed, $"{type.Name} must not depend on {parameterType.FullName}.");
            }
        }
    }

    [TestMethod]
    public void TheToolSurfaceHasNoAdministrativeOrAcquisitionOperation()
    {
        var names = typeof(IConversationTools).GetMethods().Select(method => method.Name).Order().ToArray();

        Assert.AreEqual(
            "CheckLibraryAsync,CreateBookRequestAsync,GetBookDetailsAsync,GetMyRequestsAsync,ResolveWorkAsync,SearchBooksAsync",
            string.Join(",", names),
            "A new conversation action needs an explicit design and security review.");
        Assert.IsFalse(names.Any(name =>
            name.Contains("Approve", StringComparison.OrdinalIgnoreCase) || name.Contains("Override", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Publish", StringComparison.OrdinalIgnoreCase) || name.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Download", StringComparison.OrdinalIgnoreCase) || name.Contains("Discard", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ConversationCodeNeverMentionsAdministratorAuthority()
    {
        // The engine has no administrator concept: a verified Matrix link grants a member's authority, never an admin's.
        foreach (var type in ConversationTypes())
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.IsFalse(members.Any(member => member.Name.Contains("Admin", StringComparison.OrdinalIgnoreCase)), type.Name);
        }

        Assert.IsFalse(typeof(ConversationBookTools).GetConstructors().Single().GetParameters()
            .Any(parameter => parameter.ParameterType == typeof(ICurrentAdministrator)));
    }
}
