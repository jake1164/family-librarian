using FamilyLibrarian.Application.ModelGateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.ModelGateway;

/// <summary>
/// Temporary diagnostic: writes replies that validation rejected to the application log. Enabled only by
/// <c>ModelGateway:Debug:CaptureRejectedReplies</c> (environment <c>ModelGateway__Debug__CaptureRejectedReplies</c>), off by default.
/// Prompts, excerpts and credentials are never logged, and the reply is already bounded and single-line.
/// </summary>
public sealed partial class LoggingRejectedModelReplySink(ILogger<LoggingRejectedModelReplySink> logger, IConfiguration configuration)
    : IRejectedModelReplySink
{
    private readonly bool enabled = configuration.GetValue<bool>("ModelGateway:Debug:CaptureRejectedReplies");

    public bool IsEnabled => enabled;

    public void Record(RejectedModelReply reply) =>
        LogRejected(logger, reply.Role, reply.Outcome, reply.Reason, reply.MediaAssetId, reply.Context ?? "-", reply.Reply);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning,
        Message = "Rejected model reply role={Role} outcome={Outcome} reason={Reason} asset={AssetId} evidence=[{Context}] reply={Reply}")]
    private static partial void LogRejected(
        ILogger logger, FamilyLibrarian.Domain.ModelGateway.ModelRole role, FamilyLibrarian.Domain.ModelGateway.ModelCallOutcome outcome,
        FamilyLibrarian.Domain.ModelGateway.ModelFailureReason? reason, Guid? assetId, string context, string reply);
}
