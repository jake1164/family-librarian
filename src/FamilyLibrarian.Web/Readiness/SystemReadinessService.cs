using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Operations;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Web.Readiness;

/// <summary>
/// Aggregates every enabled source/destination into the one plain signal the
/// status footer shows every user. Lives in the Web project (rather than
/// Application) because it names the Gutenberg provider's well-known id from
/// <see cref="ProviderRegistry"/>, the same layering
/// <see cref="Gutenberg.GutenbergCatalogHostedService"/> already uses.
/// </summary>
/// <remarks>
/// Deliberately conservative: a source that is enabled but has simply never
/// been tested yet is not counted as degraded, only one enabled and
/// confirmed failing (or, for Gutenberg, confirmed not yet ready). The
/// per-component breakdown in <see cref="DegradedSystemComponentResponse"/>
/// exists so an admin viewer's footer tooltip can name what needs attention
/// instead of just "sources" -- the client is responsible for hiding that
/// detail from non-admin viewers.
/// </remarks>
public sealed class SystemReadinessService(
    IProviderRegistry providerRegistry,
    IProviderSettingsStore providerSettings,
    IGutenbergCatalog gutenbergCatalog,
    IExternalProviderStore externalProviders,
    ICwaSettingsStore cwaSettings,
    IAudiobookshelfSettingsStore audiobookshelfSettings)
{
    public async Task<SystemReadinessResponse> GetReadinessAsync(CancellationToken cancellationToken)
    {
        var degraded = new List<DegradedSystemComponentResponse>();

        var gutenbergDescriptor = providerRegistry.Find(ProviderRegistry.GutenbergProviderId);
        if (gutenbergDescriptor is not null)
        {
            var gutenbergSetting = await providerSettings.FindAsync(gutenbergDescriptor.Id, cancellationToken);
            if (ProviderState.IsEnabled(gutenbergDescriptor, gutenbergSetting))
            {
                var status = await gutenbergCatalog.GetStatusAsync(cancellationToken);
                if (!status.IsReady)
                {
                    degraded.Add(new DegradedSystemComponentResponse(
                        SystemReadinessCategories.Source, "Project Gutenberg catalogue", status.FailureMessage));
                }
            }
        }

        var enabledExternalProviders = await externalProviders.ListEnabledAsync(cancellationToken);
        foreach (var provider in enabledExternalProviders)
        {
            // A failed last test is the hard signal. A provider that answers
            // but reports search or acquire as Degraded is also not working
            // (protocol v2 §5: operations are the more specific signal), so it
            // must not read as healthy just because the poll reached it.
            var operationsDetail = DescribeNonAvailableOperations(provider);
            if (provider.LastTestSucceeded == false || operationsDetail is not null)
            {
                degraded.Add(new DegradedSystemComponentResponse(
                    SystemReadinessCategories.Source,
                    provider.DisplayName,
                    operationsDetail is null
                        ? provider.LastTestMessage
                        : $"{operationsDetail} Open the provider's management page for the cause."));
            }
        }

        var cwa = await cwaSettings.FindAsync(cancellationToken);
        if (cwa is { IsEnabled: true, LastTestSucceeded: false })
        {
            degraded.Add(new DegradedSystemComponentResponse(
                SystemReadinessCategories.Publishing, "CWA", cwa.LastTestMessage));
        }

        var audiobookshelf = await audiobookshelfSettings.FindAsync(cancellationToken);
        if (audiobookshelf is { IsEnabled: true, LastTestSucceeded: false })
        {
            degraded.Add(new DegradedSystemComponentResponse(
                SystemReadinessCategories.Publishing, "Audiobookshelf", audiobookshelf.LastTestMessage));
        }

        return new SystemReadinessResponse(degraded.Count == 0, degraded);
    }

    /// <summary>
    /// Names each operation the provider last reported as anything other than
    /// Available, e.g. "Search is degraded." Null when both are Available or
    /// the provider has never reported them.
    /// </summary>
    private static string? DescribeNonAvailableOperations(ExternalProvider provider)
    {
        var parts = new List<string>();
        AddIfNotAvailable(parts, "Search", provider.CachedSearchOperationStatus);
        AddIfNotAvailable(parts, "Acquire", provider.CachedAcquireOperationStatus);
        return parts.Count == 0 ? null : string.Join(" ", parts);

        static void AddIfNotAvailable(List<string> parts, string name, string? status)
        {
            if (!string.IsNullOrWhiteSpace(status)
                && !string.Equals(status, nameof(ProviderOperationalStatus.Available), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{name} is {status.ToLowerInvariant()}.");
            }
        }
    }
}
