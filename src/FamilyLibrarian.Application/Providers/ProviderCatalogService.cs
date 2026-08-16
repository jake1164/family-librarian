using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Admin commands for repository catalogs — discovery listings, not an
/// installer. Nothing here ever creates an <see cref="ExternalProvider"/>;
/// registering a running instance stays the separate, manual
/// <see cref="ExternalProviderAdminService"/> flow.
/// </summary>
public sealed class ProviderCatalogService(
    IProviderCatalogStore store,
    IProviderCatalogFetcher fetcher,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task<IReadOnlyList<ProviderCatalog>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAsync(cancellationToken);

    public async Task<ProviderCatalogCommandResult> AddAsync(
        string url, string? displayName, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ProviderCatalogCommandResult.Invalid("Enter a valid http(s) catalog URL.");
        }

        var catalog = new ProviderCatalog(url, displayName, clock.UtcNow);
        store.Add(catalog);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ProviderCatalogAdded, AuditSubjectTypes.ProviderCatalog, catalog.Id.ToString(),
            new { catalog.Url }, cancellationToken);

        await RefreshAsync(catalog.Id, cancellationToken);
        return ProviderCatalogCommandResult.Success();
    }

    public async Task<ProviderCatalogCommandResult> SetEnabledAsync(
        Guid id, bool isEnabled, CancellationToken cancellationToken)
    {
        var catalog = await store.FindAsync(id, cancellationToken);
        if (catalog is null)
        {
            return ProviderCatalogCommandResult.Invalid("That catalog no longer exists.");
        }

        catalog.SetEnabled(isEnabled, currentUser.UserId, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        return ProviderCatalogCommandResult.Success();
    }

    public async Task<ProviderCatalogCommandResult> RefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var catalog = await store.FindAsync(id, cancellationToken);
        if (catalog is null)
        {
            return ProviderCatalogCommandResult.Invalid("That catalog no longer exists.");
        }

        var outcome = await fetcher.FetchAsync(catalog.Url, cancellationToken);
        if (outcome.Succeeded)
        {
            catalog.RecordFetchSucceeded(outcome.EntriesJson!, currentUser.UserId, clock.UtcNow);
        }
        else
        {
            catalog.RecordFetchFailed(outcome.Message, currentUser.UserId, clock.UtcNow);
        }

        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ProviderCatalogRefreshed, AuditSubjectTypes.ProviderCatalog, id.ToString(),
            new { catalog.Url, outcome.Succeeded }, cancellationToken);

        return ProviderCatalogCommandResult.Success();
    }

    public async Task<ProviderCatalogCommandResult> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var catalog = await store.FindAsync(id, cancellationToken);
        if (catalog is null)
        {
            return ProviderCatalogCommandResult.Invalid("That catalog no longer exists.");
        }

        store.Remove(catalog);
        await store.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ProviderCatalogRemoved, AuditSubjectTypes.ProviderCatalog, id.ToString(),
            new { catalog.Url }, cancellationToken);

        return ProviderCatalogCommandResult.Success();
    }
}

/// <summary>Fetches and parses a repository catalog document. No signature verification in this pass — see the M13 plan.</summary>
public interface IProviderCatalogFetcher
{
    Task<ProviderCatalogFetchOutcome> FetchAsync(string url, CancellationToken cancellationToken);
}

public sealed record ProviderCatalogFetchOutcome(bool Succeeded, string Message, string? EntriesJson)
{
    public static ProviderCatalogFetchOutcome Success(string entriesJson) => new(true, "Fetched.", entriesJson);

    public static ProviderCatalogFetchOutcome Failure(string message) => new(false, message, null);
}

public sealed record ProviderCatalogCommandResult(bool Succeeded, string? Error)
{
    public static ProviderCatalogCommandResult Success() => new(true, null);

    public static ProviderCatalogCommandResult Invalid(string error) => new(false, error);
}
