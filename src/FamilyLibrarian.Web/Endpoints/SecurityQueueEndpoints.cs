using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Security;

namespace FamilyLibrarian.Web.Endpoints;

internal static class SecurityQueueEndpoints
{
    public static void MapSecurityQueueEndpoints(this IEndpointRouteBuilder app)
    {
        // The security gate's admin surface: run the scanner/validator pass over a
        // quarantined asset, then approve or reject it. Approve is the only path that
        // can move an asset to Trusted; the service re-enforces that a Failed
        // evaluation can never be approved even by an administrator.
        var adminMediaAssets = app.MapGroup("/api/v1/admin/media-assets")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminMediaAssets.MapGet("/", ListMediaAssetsAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/evaluate", EvaluateMediaAssetAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/approve", ApproveMediaAssetAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/reject", RejectMediaAssetAsync);
    }

    private static async Task<IResult> ListMediaAssetsAsync(
        MediaAssetQueueService queue,
        CancellationToken cancellationToken)
    {
        var entries = await queue.ListAsync(cancellationToken);
        return Results.Ok(new MediaAssetAdminListResponse(entries.Select(ToMediaAssetAdminResponse).ToArray()));
    }

    private static MediaAssetAdminResponse ToMediaAssetAdminResponse(MediaAssetQueueEntry entry) => new(
        entry.Asset.AssetId,
        entry.Asset.RequestId,
        entry.Asset.WorkId,
        entry.Asset.WorkTitle,
        entry.Asset.MediaType.ToString(),
        entry.Asset.Format,
        entry.Asset.OriginalFilename,
        entry.Asset.SizeBytes,
        entry.Asset.StorageState.ToString(),
        entry.Asset.CreatedAtUtc,
        entry.Asset.UpdatedAtUtc,
        entry.LatestEvaluation is null ? null : new SecurityEvaluationDetailResponse(
            entry.LatestEvaluation.Id,
            entry.LatestEvaluation.Status.ToString(),
            entry.LatestEvaluation.CreatedAtUtc,
            entry.LatestEvaluation.CompletedAtUtc,
            entry.LatestEvaluation.ScanResults.Select(result => new SecurityScanResultResponse(
                result.ScannerId, result.IsRequired, result.Status.ToString(), result.ThreatName, result.ScannedAtUtc))
                .ToArray(),
            entry.LatestEvaluation.ValidationResults.Select(result => new FormatValidationResultResponse(
                result.ValidatorId, result.IsValid, result.Message, result.ValidatedAtUtc))
                .ToArray(),
            entry.LatestEvaluation.Approvals.Select(approval => new SecurityApprovalResponse(
                approval.Decision.ToString(), approval.ActorType.ToString(), approval.Reason, approval.DecidedAtUtc))
                .ToArray()));

    private static async Task<IResult> EvaluateMediaAssetAsync(
        Guid assetId,
        SecurityEvaluationService evaluationService,
        CancellationToken cancellationToken)
    {
        var result = await evaluationService.EvaluateAsync(assetId, cancellationToken);

        return result.Outcome switch
        {
            SecurityEvaluationOutcome.Success => Results.Ok(new SecurityEvaluationResponse(
                result.EvaluationId!.Value,
                assetId,
                result.Status!.Value.ToString(),
                result.CreatedAtUtc!.Value,
                result.CompletedAtUtc)),
            SecurityEvaluationOutcome.NotFound => Results.NotFound(),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["asset"] = [result.Error ?? "That asset could not be evaluated."]
            })
        };
    }

    private static async Task<IResult> ApproveMediaAssetAsync(
        Guid assetId,
        ApprovalDecisionRequest request,
        ApprovalService approvals,
        CancellationToken cancellationToken) =>
        ToApprovalResult(await approvals.ApproveAsync(assetId, request.Reason, cancellationToken));

    private static async Task<IResult> RejectMediaAssetAsync(
        Guid assetId,
        ApprovalDecisionRequest request,
        ApprovalService approvals,
        CancellationToken cancellationToken) =>
        ToApprovalResult(await approvals.RejectAsync(assetId, request.Reason, cancellationToken));

    private static IResult ToApprovalResult(ApprovalResult result) => result.Outcome switch
    {
        ApprovalOutcome.Success => Results.NoContent(),
        ApprovalOutcome.NotFound => Results.NotFound(),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["asset"] = [result.Error ?? "That decision could not be recorded."]
        })
    };
}
