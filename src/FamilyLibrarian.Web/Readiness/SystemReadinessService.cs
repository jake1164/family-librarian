using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Operations;
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
}
