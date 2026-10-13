using FamilyLibrarian.Application.Conversation;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Conversation;

/// <summary>Writes where chat time goes to the application log: step name, outcome word and milliseconds, never any content.</summary>
public sealed partial class ConversationDiagnostics(ILogger<ConversationDiagnostics> logger) : IConversationDiagnostics
{
    /// <summary>A step slower than this is logged as a warning so it stands out.</summary>
    public static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(10);

    public void StepCompleted(string stepName, TimeSpan elapsed, string outcome)
    {
        var milliseconds = (long)elapsed.TotalMilliseconds;
        if (elapsed >= SlowStep)
        {
            LogSlowStep(stepName, outcome, milliseconds);
        }
        else
        {
            LogStep(stepName, outcome, milliseconds);
        }
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "conversation.step {Step} {Outcome} in {ElapsedMs} ms")]
    private partial void LogStep(string step, string outcome, long elapsedMs);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Warning, Message = "conversation.step.slow {Step} {Outcome} in {ElapsedMs} ms")]
    private partial void LogSlowStep(string step, string outcome, long elapsedMs);
}
