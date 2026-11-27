using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Infrastructure.Gutenberg;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Gutenberg;

/// <summary>One host owns catalogue work; accepted refreshes survive a browser disconnect or host restart.</summary>
internal sealed class GutenbergCatalogRefreshCoordinator(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);

    public async Task<GutenbergRefreshRequestResult> QueueAsync(bool onlyWhenMissing, CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken))
            return new(false, null, "The Project Gutenberg catalogue already has an operation in progress.");
        var queued = false;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            if (!await IsEnabledAsync(services, cancellationToken))
                return new(false, null, "Project Gutenberg is disabled. Enable it before refreshing the catalogue.");

            var database = services.GetRequiredService<AppDbContext>();
            var state = await GetStateAsync(database, cancellationToken);
            if (GutenbergCatalogOperations.IsInProgress(state.Status))
                return new(false, null, "The Project Gutenberg catalogue already has an operation in progress.");
            if (onlyWhenMissing && state.ActiveGenerationId is not null)
                return new(false, null, null);

            state.Status = "Queued";
            state.LastAttemptUtc = timeProvider.GetUtcNow();
            state.LastProgressUtc = state.LastAttemptUtc;
            state.FailureMessage = null;
            state.ProgressMessage = "Refresh queued. You can leave this page; the server will continue the refresh.";
            state.DownloadedBytes = 0;
            state.LastArchiveSizeBytes = null;
            state.SourceHost = null;
            state.InProgressBookCount = 0;
            state.InProgressFormatCount = 0;
            await database.SaveChangesAsync(cancellationToken);
            queued = true;
            return new(true, await services.GetRequiredService<IGutenbergCatalog>().GetStatusAsync(cancellationToken), null);
        }
        finally
        {
            _operation.Release();
            // Wake only after the reservation is released. Otherwise the worker
            // can consume the wakeup, find the gate occupied and sleep all day.
            if (queued) _signal.Release();
        }
    }

    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await database.Set<GutenbergCatalogSyncStateEntity>().SingleOrDefaultAsync(cancellationToken);
            // Queued work has not started and can safely be picked up by this host.
            if (state is not null && state.Status != "Queued" && GutenbergCatalogOperations.IsInProgress(state.Status))
            {
                state.Status = "Failed";
                state.FailureMessage = "The previous catalogue operation was interrupted when the application stopped. Refresh again to retry.";
                state.ProgressMessage = null;
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        finally { _operation.Release(); }
    }

    public async Task RunQueuedAsync(CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken)) return;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<AppDbContext>();
            var state = await GetStateAsync(database, cancellationToken);
            if (state.Status != "Queued") return;
            try
            {
                if (!await IsEnabledAsync(services, cancellationToken))
                    throw new InvalidOperationException("Project Gutenberg was disabled before the queued refresh started. Enable it and refresh again.");
                // Manual and scheduled refreshes deliberately share this exact path,
                // including full reconciliation and all configured fallback sources.
                await services.GetRequiredService<IGutenbergCatalogSynchronizer>().SynchronizeIncrementalAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                database.ChangeTracker.Clear();
                var failed = await GetStateAsync(database, CancellationToken.None);
                failed.Status = "Failed";
                failed.FailureMessage = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? "The catalogue refresh was interrupted when the application stopped. Refresh again to retry."
                    : exception.Message[..Math.Min(exception.Message.Length, 2_000)];
                failed.ProgressMessage = null;
                await database.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _operation.Release(); }
    }

    public async Task<GutenbergCatalogPurgeResult?> TryPurgeAsync(CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken)) return null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var current = await services.GetRequiredService<IGutenbergCatalog>().GetStatusAsync(cancellationToken);
            if (GutenbergCatalogOperations.IsInProgress(current.Status)) return null;
            return await services.GetRequiredService<IGutenbergCatalogMaintenance>().PurgeAsync(CancellationToken.None);
        }
        finally { _operation.Release(); }
    }

    public Task<bool> WaitForWorkAsync(TimeSpan delay, CancellationToken cancellationToken) => _signal.WaitAsync(delay, cancellationToken);

    private static Task<bool> IsEnabledAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        ProviderState.IsUsableAsync(services.GetRequiredService<IProviderRegistry>(),
            services.GetRequiredService<IProviderSettingsStore>(), ProviderRegistry.GutenbergProviderId, cancellationToken);

    private static async Task<GutenbergCatalogSyncStateEntity> GetStateAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        var state = await database.Set<GutenbergCatalogSyncStateEntity>().SingleOrDefaultAsync(cancellationToken);
        if (state is not null) return state;
        state = new GutenbergCatalogSyncStateEntity();
        database.Add(state);
        return state;
    }

    public void Dispose()
    {
        _operation.Dispose();
        _signal.Dispose();
    }
}

internal sealed record GutenbergRefreshRequestResult(bool Accepted, GutenbergCatalogStatus? Status, string? Error);
