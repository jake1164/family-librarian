using FamilyLibrarian.Application.SemanticAssessment;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Drains the shadow buffer one item at a time (alpha.6 M4-3). It runs entirely off the request and acquisition paths: the
/// callers only drop evidence into a bounded buffer, and a model that is slow, off or broken costs them nothing. The
/// model-execution policy still applies its own budgets, timeout and circuit breaker to every item.
/// </summary>
public sealed partial class CandidateSemanticShadowHostedService(
    CandidateSemanticShadowBuffer buffer,
    IServiceScopeFactory scopeFactory,
    ILogger<CandidateSemanticShadowHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in buffer.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var analyzer = scope.ServiceProvider.GetRequiredService<CandidateSemanticAnalyzer>();
                    var report = await analyzer.AnalyzeAsync(work.RequestId, work.Assessment, stoppingToken);
                    if (report.Error is { } error)
                    {
                        LogAnalysisFailed(error);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogAnalysisFailed(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Warning,
        Message = "Shadow semantic analysis of a candidate failed and was ignored.")]
    private partial void LogAnalysisFailed(Exception exception);
}
