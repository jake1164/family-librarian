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
        var deletedBookCount = await database.GutenbergCatalogBooks.CountAsync(cancellationToken);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.GutenbergCatalogBooks.ExecuteDeleteAsync(cancellationToken);
        await database.GutenbergCatalogSyncStates.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();

        await audit.WriteAsync(
            AuditActions.GutenbergCatalogPurged,
            AuditSubjectTypes.Provider,
            ProviderRegistry.GutenbergProviderId,
            new { DeletedBookCount = deletedBookCount },
            cancellationToken);

        return new GutenbergCatalogPurgeResult(deletedBookCount);
    }
}
