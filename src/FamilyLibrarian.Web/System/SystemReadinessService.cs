using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Web.System;

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
/// detailed per-source picture belongs to admins on the Tasks page, not the
/// footer every family member sees.
/// </remarks>
public sealed class SystemReadinessService(
    IProviderRegistry providerRegistry,
    IProviderSettingsStore providerSettings,
    IGutenbergCatalog gutenbergCatalog,
    ICwaSettingsStore cwaSettings,
    IAudiobookshelfSettingsStore audiobookshelfSettings)
{
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var gutenbergDescriptor = providerRegistry.Find(ProviderRegistry.GutenbergProviderId);
        if (gutenbergDescriptor is not null)
        {
            var gutenbergSetting = await providerSettings.FindAsync(gutenbergDescriptor.Id, cancellationToken);
            if (ProviderState.IsEnabled(gutenbergDescriptor, gutenbergSetting))
            {
                var status = await gutenbergCatalog.GetStatusAsync(cancellationToken);
                if (!status.IsReady)
                {
                    return false;
                }
            }
        }

        var cwa = await cwaSettings.FindAsync(cancellationToken);
        if (cwa is { IsEnabled: true, LastTestSucceeded: false })
        {
            return false;
        }

        var audiobookshelf = await audiobookshelfSettings.FindAsync(cancellationToken);
        if (audiobookshelf is { IsEnabled: true, LastTestSucceeded: false })
        {
            return false;
        }

        return true;
    }
}
