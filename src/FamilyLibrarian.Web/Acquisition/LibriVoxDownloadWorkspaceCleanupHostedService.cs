using FamilyLibrarian.Infrastructure.Acquisition;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>Removes expired partial archive workspaces once before acquisition workers start.</summary>
public sealed partial class LibriVoxDownloadWorkspaceCleanupHostedService(
    LibriVoxDownloadWorkspace workspace,
    ILogger<LibriVoxDownloadWorkspaceCleanupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var removed = workspace.CleanupExpired(DateTimeOffset.UtcNow);
        if (removed > 0)
        {
            LogRemovedWorkspaceCount(removed);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Removed {WorkspaceCount} expired audiobook download workspaces.")]
    private partial void LogRemovedWorkspaceCount(int workspaceCount);
}
