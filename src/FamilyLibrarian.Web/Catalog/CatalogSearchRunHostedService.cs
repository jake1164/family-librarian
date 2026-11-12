using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;

namespace FamilyLibrarian.Web.Catalog;

public sealed partial class CatalogSearchRunHostedService(
    CatalogSearchRunCoordinator coordinator,
    IServiceScopeFactory scopeFactory,
    ILogger<CatalogSearchRunHostedService> logger) : BackgroundService
{
    private readonly SemaphoreSlim concurrency = new(4, 4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var run in coordinator.ReadPendingAsync(stoppingToken))
        {
            await concurrency.WaitAsync(stoppingToken);
            _ = ProcessAsync(run, stoppingToken);
        }
    }

    private async Task ProcessAsync(CatalogSearchRun run, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, run.CancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IActiveMetadataProviderResolver>();
            var search = scope.ServiceProvider.GetRequiredService<ICatalogSearchService>();
            var providers = await resolver.GetActiveProvidersAsync(linked.Token);
            await Task.WhenAll(providers.Select(provider => SearchProviderAsync(provider, search, run, linked.Token)));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) { LogRunFailed(exception, run.Id); }
        finally
        {
            run.Complete();
            run.Dispose();
            concurrency.Release();
        }
    }

    private async Task SearchProviderAsync(IBookMetadataProvider provider, ICatalogSearchService search, CatalogSearchRun run, CancellationToken token)
    {
        if (run.HasPreviousPage && run.Snapshot().Any(result => result.ProviderId == provider.Id && result.Succeeded && !result.HasMore))
            return;
        try
        {
            var result = await search.SearchProviderAsync(provider, run.Query, tolerateUnexpectedFailures: true, token);
            if (result.Failure is { } failure) LogProviderFailed(failure.Exception, provider.Id);
            run.Add(result.ProviderId, result.ProviderName, result.Succeeded, result.Candidates, result.HasMore);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Catalog search run {CatalogSearchRunId} failed.")]
    private partial void LogRunFailed(Exception exception, Guid catalogSearchRunId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Metadata provider {ProviderId} failed during progressive catalog search.")]
    private partial void LogProviderFailed(Exception exception, string providerId);
}
