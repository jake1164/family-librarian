using FamilyLibrarian.Application.SemanticAssessment;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Drains the artifact shadow buffer one asset at a time (alpha.6 M4-4). Entirely off the security and approval paths: those
/// only note that evidence changed, never wait for, and never read, what this produces.
/// </summary>
public sealed partial class ArtifactSemanticShadowHostedService(
    ArtifactSemanticShadowBuffer buffer,
    IServiceScopeFactory scopeFactory,
    ILogger<ArtifactSemanticShadowHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var assetId in buffer.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var analyzer = scope.ServiceProvider.GetRequiredService<ArtifactSemanticAnalyzer>();
                    var report = await analyzer.AnalyzeAsync(assetId, stoppingToken);
                    if (report.Error is { } error)
                    {
                        LogAnalysisFailed(error, assetId);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogAnalysisFailed(exception, assetId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Warning,
        Message = "Shadow semantic analysis of media asset {AssetId} failed and was ignored.")]
    private partial void LogAnalysisFailed(Exception exception, Guid assetId);
}
