using FamilyLibrarian.Contracts.Realtime;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Realtime;

internal sealed partial class LiveUpdatesPublisher(
    IServiceScopeFactory scopes,
    LiveConnections connections,
    IHubContext<LiveUpdatesHub> hub,
    ILogger<LiveUpdatesPublisher> logger)
{
    public async Task PublishAsync(LiveChanges changes)
    {
        if (!changes.HasChanges) return;
        var connected = connections.Snapshot();
        if (connected.Length == 0) return;

        try
        {
            // The write is committed. Use a fresh context to avoid re-entering
            // SaveChanges or querying through an already-completed transaction.
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = timeout.Token;
            var userIds = connected.Select(connection => connection.UserId).Distinct().ToArray();
            var users = await database.Users.AsNoTracking().Where(user => userIds.Contains(user.Id))
                .Select(user => new { user.Id, user.Status, user.SecurityStamp }).ToDictionaryAsync(user => user.Id, token);
            var admins = await (from userRole in database.UserRoles
                                join role in database.Roles on userRole.RoleId equals role.Id
                                where role.Name == "Admin" && userIds.Contains(userRole.UserId)
                                select userRole.UserId).ToArrayAsync(token);

            if (changes.EvaluationIds.Count > 0)
                changes.AssetIds.UnionWith(await database.SecurityEvaluations
                    .Where(evaluation => changes.EvaluationIds.Contains(evaluation.Id))
                    .Select(evaluation => evaluation.AssetId).ToArrayAsync(token));
            if (changes.AssetIds.Count + changes.BundleIds.Count > 0)
                changes.FormatIds.UnionWith(await database.MediaAssets
                    .Where(asset => changes.AssetIds.Contains(asset.Id) ||
                        (asset.BundleId != null && changes.BundleIds.Contains(asset.BundleId.Value)))
                    .Select(asset => asset.AssociatedRequestFormatId).ToArrayAsync(token));
            if (changes.FormatIds.Count > 0)
                changes.RequestIds.UnionWith(await database.RequestFormats
                    .Where(format => changes.FormatIds.Contains(format.Id))
                    .Select(format => format.RequestId).ToArrayAsync(token));
            if (changes.JobIds.Count > 0)
                changes.RequestIds.UnionWith(await database.AcquisitionJobs
                    .Where(job => changes.JobIds.Contains(job.Id)).Select(job => job.RequestId).ToArrayAsync(token));
            if (changes.RequestIds.Count > 0)
                foreach (var owner in await database.BookRequests.Where(request => changes.RequestIds.Contains(request.Id))
                             .Select(request => request.UserId).Distinct().ToArrayAsync(token))
                    changes.ForUser(owner, LiveUpdateTopics.Requests);

            foreach (var connection in connected)
            {
                if (!users.TryGetValue(connection.UserId, out var user) || user.Status != UserStatus.Active ||
                    !string.Equals(user.SecurityStamp, connection.SecurityStamp, StringComparison.Ordinal))
                {
                    connection.Context.Abort();
                    continue;
                }

                var topics = changes.SharedTopics | changes.UserTopics.GetValueOrDefault(user.Id);
                if (admins.Contains(user.Id)) topics |= changes.AdminTopics;
                if (topics != LiveUpdateTopics.None)
                    await hub.Clients.Client(connection.Context.ConnectionId).SendAsync(LiveUpdates.Changed, topics, token);
            }
        }
        catch (Exception exception)
        {
            // A notification failure cannot roll back committed business work.
            // Reconnection or the shared Refresh button reloads API snapshots.
            LogPublishFailure(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish live updates.")]
    private static partial void LogPublishFailure(ILogger logger, Exception exception);
}
