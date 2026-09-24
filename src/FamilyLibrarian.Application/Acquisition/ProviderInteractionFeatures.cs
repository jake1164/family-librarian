namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Reads the negotiated protocol-v2 feature strings (docs/04 §4/§8) off a
/// provider's cached manifest capabilities. Shared by
/// <see cref="ProviderInteractionService"/> and
/// <see cref="ProviderRemoteViewBrokerService"/> so the two control-plane and
/// view-plane checks can't quietly drift apart.
/// </summary>
internal static class ProviderInteractionFeatures
{
    private const string InteractionControlFeature = "waiting-interaction-control";
    private const string InteractionViewFeature = "interaction-view";

    public static bool SupportsInteractionControl(Domain.Providers.ExternalProvider provider) =>
        HasFeature(provider, InteractionControlFeature);

    /// <summary>
    /// docs/04 §4: a provider must not declare <c>interaction-view</c>
    /// without <c>waiting-interaction-control</c>, so this checks both --
    /// deliberately tolerant of a provider that gets that ordering wrong
    /// rather than trusting its self-declaration alone.
    /// </summary>
    public static bool SupportsInteractionView(Domain.Providers.ExternalProvider provider) =>
        HasFeature(provider, InteractionControlFeature) && HasFeature(provider, InteractionViewFeature);

    private static bool HasFeature(Domain.Providers.ExternalProvider provider, string feature)
    {
        var features = provider.CachedCapabilities?
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(section => section.StartsWith("features:", StringComparison.Ordinal));

        return features is not null &&
            features["features:".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(candidate => string.Equals(candidate.Trim(), feature, StringComparison.OrdinalIgnoreCase));
    }
}
