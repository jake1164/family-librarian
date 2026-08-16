using FamilyLibrarian.Application.Policy;
using FamilyLibrarian.Domain.Policy;

namespace FamilyLibrarian.Infrastructure.Policy;

/// <summary>
/// The fixed set of acquisition-policy profiles compiled into this build.
/// </summary>
/// <remarks>
/// Hardcoded on purpose, same as <c>ProviderRegistry</c>: there is no path by
/// which configuration or a request body introduces a new ranking strategy.
/// </remarks>
public sealed class PolicyProfileRegistry : IPolicyProfileRegistry
{
    private static readonly PolicyProfileDescriptor[] Profiles =
    [
        new PolicyProfileDescriptor(
            PolicyProfileIds.ManualChoice,
            "Manual Choice",
            "No automatic recommendation. Every option is shown as-is; the librarian decides."),
        new PolicyProfileDescriptor(
            PolicyProfileIds.LibraryFirst,
            "Library First",
            "Prefer a copy already owned, then one available to borrow, before anything else."),
        new PolicyProfileDescriptor(
            PolicyProfileIds.FreeFirst,
            "Free First",
            "Prefer any free option (owned, borrowed, or a free direct download) over a paid one."),
        new PolicyProfileDescriptor(
            PolicyProfileIds.LowestCost,
            "Lowest Cost",
            "Recommend whichever option costs the least, regardless of source.")
    ];

    public IReadOnlyList<PolicyProfileDescriptor> GetProfiles() => Profiles;

    public PolicyProfileDescriptor? Find(string profileId) =>
        string.IsNullOrWhiteSpace(profileId)
            ? null
            : Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
}
