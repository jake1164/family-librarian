using System.Text.Json;
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
            var providers = await resolver.GetActiveProvidersAsync(linked.Token);
            await Task.WhenAll(providers.Select(provider => SearchProviderAsync(provider, run, linked.Token)));
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

    private async Task SearchProviderAsync(IBookMetadataProvider provider, CatalogSearchRun run, CancellationToken token)
    {
        try
        {
            var page = await provider.SearchAsync(run.Query, token);
            run.Add(provider.Id, provider.DisplayName, true, page.Candidates, page.HasMore);
        }
        catch (HttpRequestException exception) { LogProviderFailed(exception, provider.Id); run.Add(provider.Id, provider.DisplayName, false, [], false); }
        catch (JsonException exception) { LogProviderFailed(exception, provider.Id); run.Add(provider.Id, provider.DisplayName, false, [], false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            LogProviderFailed(exception, provider.Id);
            run.Add(provider.Id, provider.DisplayName, false, [], false);
        }
        catch (Exception exception)
        {
            LogProviderFailed(exception, provider.Id);
            run.Add(provider.Id, provider.DisplayName, false, [], false);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Catalog search run {CatalogSearchRunId} failed.")]
    private partial void LogRunFailed(Exception exception, Guid catalogSearchRunId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Metadata provider {ProviderId} failed during progressive catalog search.")]
    private partial void LogProviderFailed(Exception exception, string providerId);
}
