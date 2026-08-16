namespace FamilyLibrarian.Application.Policy;

public sealed record PolicyProfileDescriptor(string Id, string DisplayName, string Description);

/// <summary>The fixed set of acquisition-policy profiles compiled into this build.</summary>
public interface IPolicyProfileRegistry
{
    IReadOnlyList<PolicyProfileDescriptor> GetProfiles();

    PolicyProfileDescriptor? Find(string profileId);
}
