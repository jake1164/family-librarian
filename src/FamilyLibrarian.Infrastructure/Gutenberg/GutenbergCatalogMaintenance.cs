using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Gutenberg;

/// <summary>Purges locally imported Gutenberg data without affecting application history or schema.</summary>
internal sealed class GutenbergCatalogMaintenance(
    AppDbContext database,
    IAuditWriter audit) : IGutenbergCatalogMaintenance
{
    public async Task<GutenbergCatalogPurgeResult> PurgeAsync(CancellationToken cancellationToken)
    {
        var state = await database.GutenbergCatalogSyncStates
            .SingleOrDefaultAsync(item => item.Id == GutenbergCatalogSyncStateEntity.SingletonId, cancellationToken);
        if (state is null)
        {
            state = new GutenbergCatalogSyncStateEntity();
            database.GutenbergCatalogSyncStates.Add(state);
        }

        // Commit this transition before starting the potentially long-running
        // cascading delete. The status endpoint can then report progress even
        // after an administrator leaves and returns to the Sources page.
        state.Status = "Purging";
        state.FailureMessage = null;
        await database.SaveChangesAsync(cancellationToken);

        int deletedBookCount;
        try
        {
            deletedBookCount = await database.GutenbergCatalogBooks.CountAsync(cancellationToken);

            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.GutenbergCatalogBooks.ExecuteDeleteAsync(cancellationToken);
            ResetToNeverSynced(state);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            database.ChangeTracker.Clear();
        }
        catch
        {
            database.ChangeTracker.Clear();
            var failedState = await database.GutenbergCatalogSyncStates
                .SingleAsync(item => item.Id == GutenbergCatalogSyncStateEntity.SingletonId, CancellationToken.None);
            failedState.Status = "Failed";
            failedState.FailureMessage = "The local catalogue could not be deleted. Try again.";
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        await audit.WriteAsync(
            AuditActions.GutenbergCatalogPurged,
            AuditSubjectTypes.Provider,
            ProviderRegistry.GutenbergProviderId,
            new { DeletedBookCount = deletedBookCount },
            cancellationToken);

        return new GutenbergCatalogPurgeResult(deletedBookCount);
    }

    private static void ResetToNeverSynced(GutenbergCatalogSyncStateEntity state)
    {
        state.ActiveGenerationId = null;
        state.LastAttemptUtc = null;
        state.LastSuccessfulSyncUtc = null;
        state.LastSuccessfulIncrementalSyncUtc = null;
        state.LastSourceModifiedUtc = null;
        state.LastArchiveSizeBytes = null;
        state.BookCount = 0;
        state.FormatCount = 0;
        state.ParseErrorCount = 0;
        state.LastDuration = null;
        state.Status = "NeverSynced";
        state.FailureMessage = null;
    }
}
